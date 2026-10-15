package main

import (
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
)

type proxySettings struct {
	Flags               uint32
	Server, Bypass, PAC string
}
type proxyBackup struct{ Before, Installed proxySettings }

func (s proxySettings) upstream() (*url.URL, error) {
	if s.Flags&4 != 0 && s.PAC != "" {
		return nil, fmt.Errorf("测试版暂不支持 PAC 自动代理，请临时切换为手动代理后重试")
	}
	if s.Flags&2 == 0 || s.Server == "" {
		return nil, nil
	}
	address := s.Server
	if strings.Contains(address, "=") {
		address = ""
		var httpAddress, httpsAddress string
		for _, pair := range strings.Split(s.Server, ";") {
			key, value, ok := strings.Cut(pair, "=")
			if ok && key == "http" {
				httpAddress = value
			}
			if ok && key == "https" {
				httpsAddress = value
			}
			if ok && (key == "https" || (address == "" && key == "http")) {
				address = value
			}
		}
		if httpAddress != "" && httpsAddress != "" && httpAddress != httpsAddress {
			return nil, fmt.Errorf("测试版暂不支持 HTTP 与 HTTPS 使用不同上游代理，请临时使用统一代理")
		}
		if address == "" {
			return nil, fmt.Errorf("测试版需要 HTTP 上游代理，暂不支持仅 SOCKS 代理")
		}
	}
	if !strings.Contains(address, "://") {
		address = "http://" + address
	}
	u, err := url.Parse(address)
	if err != nil || u.Hostname() == "" || u.Scheme != "http" || (u.Path != "" && u.Path != "/") {
		return nil, fmt.Errorf("无法识别现有手动代理，请检查 Windows 代理设置")
	}
	return u, nil
}

func activateProxy(dir string, before proxySettings, address string) error {
	installed := proxySettings{Flags: 3, Server: "http=" + address + ";https=" + address, Bypass: before.Bypass + ";<local>;localhost;127.0.0.1"}
	backup := proxyBackup{before, installed}
	raw, err := json.Marshal(backup)
	if err != nil {
		return err
	}
	file, err := os.OpenFile(filepath.Join(dir, "proxy-backup.json"), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return fmt.Errorf("代理恢复记录尚未处理，请先点击恢复代理")
	}
	_, err = file.Write(raw)
	if err == nil {
		err = file.Sync()
	}
	closeErr := file.Close()
	if err != nil || closeErr != nil {
		_ = os.Remove(file.Name())
		return fmt.Errorf("保存原代理配置失败，未启用抓包")
	}
	if err := writeProxy(installed); err != nil {
		if recoveryErr := writeProxy(before); recoveryErr == nil {
			_ = os.Remove(file.Name())
		}
		return fmt.Errorf("设置 Windows 代理失败，请点击恢复代理后重试")
	}
	return nil
}

func restoreProxy(dir string) error {
	return restoreProxyUsing(dir, readProxy, writeProxy)
}

func restoreProxyUsing(dir string, read func() (proxySettings, error), write func(proxySettings) error) error {
	path := filepath.Join(dir, "proxy-backup.json")
	raw, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return fmt.Errorf("读取代理恢复记录失败")
	}
	var backup proxyBackup
	if json.Unmarshal(raw, &backup) != nil {
		return fmt.Errorf("代理恢复记录损坏，请手动检查 Windows 代理设置")
	}
	current, err := read()
	if err != nil {
		return err
	}
	// Respect proxy changes made by another application while capture was running.
	if current == backup.Installed {
		if err := write(backup.Before); err != nil {
			return err
		}
	}
	if err := os.Remove(path); err != nil {
		return fmt.Errorf("代理已恢复，但清理恢复记录失败")
	}
	return nil
}
