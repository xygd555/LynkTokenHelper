//go:build !windows

package main

import (
	"crypto/tls"
	"fmt"
)

func unsupported() error {
	return fmt.Errorf("测试工具仅支持 Windows，请使用 Windows 版 exe")
}
func readProxy() (proxySettings, error)        { return proxySettings{}, unsupported() }
func writeProxy(proxySettings) error           { return unsupported() }
func protect([]byte) ([]byte, error)           { return nil, unsupported() }
func unprotect([]byte) ([]byte, error)         { return nil, unsupported() }
func certificateTrusted(*tls.Certificate) bool { return false }
func installCertificate(string) error          { return unsupported() }
func removeCertificate(*tls.Certificate) error { return unsupported() }
func lockDirectory(string) (func(), error)     { return nil, unsupported() }
func showDesktopError(message string)          { fmt.Println(message) }
func showDesktopInfo(message string)           { fmt.Println(message) }
func startWatchdog(string) error               { return unsupported() }
func watchParent(int, string)                  {}
