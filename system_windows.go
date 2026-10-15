package main

import (
	"crypto/sha1"
	"crypto/tls"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

var wininet = windows.NewLazySystemDLL("wininet.dll")
var queryOption = wininet.NewProc("InternetQueryOptionW")
var setOption = wininet.NewProc("InternetSetOptionW")
var globalFree = windows.NewLazySystemDLL("kernel32.dll").NewProc("GlobalFree")

type internetOption struct {
	Option uint32
	Value  uint64
}
type internetOptionList struct {
	Size         uint32
	Connection   *uint16
	Count, Error uint32
	Options      *internetOption
}

func readProxy() (proxySettings, error) {
	options := []internetOption{{Option: 1}, {Option: 2}, {Option: 3}, {Option: 4}}
	list := internetOptionList{Count: uint32(len(options)), Options: &options[0]}
	list.Size = uint32(unsafe.Sizeof(list))
	size := list.Size
	ok, _, _ := queryOption.Call(0, 75, uintptr(unsafe.Pointer(&list)), uintptr(unsafe.Pointer(&size)))
	if ok == 0 {
		return proxySettings{}, fmt.Errorf("读取 Windows 代理设置失败")
	}
	strings := make([]string, 3)
	for i := 1; i < 4; i++ {
		if options[i].Value != 0 {
			ptr := *(**uint16)(unsafe.Pointer(&options[i].Value))
			strings[i-1] = windows.UTF16PtrToString(ptr)
			_, _, _ = globalFree.Call(uintptr(options[i].Value))
		}
	}
	return proxySettings{uint32(options[0].Value), strings[0], strings[1], strings[2]}, nil
}

func writeProxy(settings proxySettings) error {
	server, err := windows.UTF16PtrFromString(settings.Server)
	if err != nil {
		return err
	}
	bypass, err := windows.UTF16PtrFromString(settings.Bypass)
	if err != nil {
		return err
	}
	pac, err := windows.UTF16PtrFromString(settings.PAC)
	if err != nil {
		return err
	}
	options := []internetOption{{1, uint64(settings.Flags)}, {2, uint64(uintptr(unsafe.Pointer(server)))}, {3, uint64(uintptr(unsafe.Pointer(bypass)))}, {4, uint64(uintptr(unsafe.Pointer(pac)))}}
	list := internetOptionList{Count: uint32(len(options)), Options: &options[0]}
	list.Size = uint32(unsafe.Sizeof(list))
	ok, _, _ := setOption.Call(0, 75, uintptr(unsafe.Pointer(&list)), uintptr(list.Size))
	runtime.KeepAlive(server)
	runtime.KeepAlive(bypass)
	runtime.KeepAlive(pac)
	if ok == 0 {
		return fmt.Errorf("更新 Windows 代理设置失败")
	}
	_, _, _ = setOption.Call(0, 39, 0, 0)
	_, _, _ = setOption.Call(0, 37, 0, 0)
	return nil
}

func protect(raw []byte) ([]byte, error)   { return cryptData(raw, true) }
func unprotect(raw []byte) ([]byte, error) { return cryptData(raw, false) }
func cryptData(raw []byte, encrypt bool) ([]byte, error) {
	if len(raw) == 0 {
		return nil, fmt.Errorf("本机密钥为空")
	}
	input := windows.DataBlob{Size: uint32(len(raw)), Data: &raw[0]}
	var output windows.DataBlob
	var err error
	if encrypt {
		err = windows.CryptProtectData(&input, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &output)
	} else {
		err = windows.CryptUnprotectData(&input, nil, nil, 0, nil, windows.CRYPTPROTECT_UI_FORBIDDEN, &output)
	}
	if err != nil {
		return nil, fmt.Errorf("Windows 本机密钥保护操作失败")
	}
	defer windows.LocalFree(windows.Handle(unsafe.Pointer(output.Data)))
	return append([]byte(nil), unsafe.Slice(output.Data, int(output.Size))...), nil
}

func certificateTrusted(ca *tls.Certificate) bool {
	name, _ := windows.UTF16PtrFromString("ROOT")
	store, err := windows.CertOpenStore(windows.CERT_STORE_PROV_SYSTEM, 0, 0, windows.CERT_SYSTEM_STORE_CURRENT_USER, uintptr(unsafe.Pointer(name)))
	runtime.KeepAlive(name)
	if err != nil {
		return false
	}
	defer windows.CertCloseStore(store, 0)
	hash := sha1.Sum(ca.Certificate[0])
	blob := windows.DataBlob{Size: uint32(len(hash)), Data: &hash[0]}
	cert, err := windows.CertFindCertificateInStore(store, windows.X509_ASN_ENCODING, 0, windows.CERT_FIND_SHA1_HASH, unsafe.Pointer(&blob), nil)
	if err != nil {
		return false
	}
	_ = windows.CertFreeCertificateContext(cert)
	return true
}

func hiddenCommand(name string, args ...string) *exec.Cmd {
	cmd := exec.Command(name, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true}
	return cmd
}
func installCertificate(path string) error {
	if err := hiddenCommand("certutil.exe", "-user", "-addstore", "Root", path).Run(); err != nil {
		return fmt.Errorf("安装证书失败，请检查当前用户的证书管理权限")
	}
	return nil
}
func removeCertificate(ca *tls.Certificate) error {
	if !certificateTrusted(ca) {
		return nil
	}
	hash := sha1.Sum(ca.Certificate[0])
	if err := hiddenCommand("certutil.exe", "-user", "-delstore", "Root", hex.EncodeToString(hash[:])).Run(); err != nil {
		return fmt.Errorf("移除本工具证书失败")
	}
	return nil
}

func lockDirectory(dir string) (func(), error) {
	file, err := os.OpenFile(filepath.Join(dir, "helper.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	var overlap windows.Overlapped
	if err := windows.LockFileEx(windows.Handle(file.Fd()), windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &overlap); err != nil {
		_ = file.Close()
		return nil, err
	}
	return func() { _ = windows.UnlockFileEx(windows.Handle(file.Fd()), 0, 1, 0, &overlap); _ = file.Close() }, nil
}

func startWatchdog(dir string) error {
	executable, err := os.Executable()
	if err != nil {
		return err
	}
	ready := filepath.Join(dir, "watchdog-"+strconv.Itoa(os.Getpid())+".ready")
	_ = os.Remove(ready)
	cmd := hiddenCommand(executable, "--data-dir", dir, "--watch-parent", strconv.Itoa(os.Getpid()))
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("启动代理恢复进程失败，未启用抓包")
	}
	_ = cmd.Process.Release()
	for i := 0; i < 100; i++ {
		if _, err := os.Stat(ready); err == nil {
			_ = os.Remove(ready)
			return nil
		}
		time.Sleep(30 * time.Millisecond)
	}
	return fmt.Errorf("代理恢复进程未就绪，未启用抓包")
}

func watchParent(pid int, dir string) {
	process, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		return
	}
	defer windows.CloseHandle(process)
	ready := filepath.Join(dir, "watchdog-"+strconv.Itoa(pid)+".ready")
	if os.WriteFile(ready, []byte("ready"), 0600) != nil {
		return
	}
	_, _ = windows.WaitForSingleObject(process, windows.INFINITE)
	unlock, err := lockDirectory(dir)
	if err != nil {
		return
	}
	defer unlock()
	_ = restoreProxy(dir)
	_ = os.Remove(ready)
}
