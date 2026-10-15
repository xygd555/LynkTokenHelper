package main

import (
	"golang.org/x/sys/windows"
	"unsafe"
)

func showDesktopMessage(message string, kind uintptr) {
	body, _ := windows.UTF16PtrFromString(message)
	title, _ := windows.UTF16PtrFromString("领克凭据助手")
	_, _, _ = windows.NewLazySystemDLL("user32.dll").NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(body)), uintptr(unsafe.Pointer(title)), kind)
}
func showDesktopError(message string) { showDesktopMessage(message, 0x10) }
func showDesktopInfo(message string)  { showDesktopMessage(message, 0x40) }
