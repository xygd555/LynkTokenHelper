package main

import (
	"encoding/json"
	"flag"
	"os"
	"path/filepath"
)

// defaultServer is supplied by the build script.
var defaultServer string

func main() {
	executable, _ := os.Executable()
	dir := flag.String("data-dir", filepath.Join(filepath.Dir(executable), "state"), "本机状态目录")
	watch := flag.Int("watch-parent", 0, "等待主进程退出后恢复代理")
	restore := flag.Bool("restore-proxy", false, "恢复本工具修改的代理后退出")
	stdio := flag.Bool("stdio", false, "通过进程管道连接桌面窗口")
	flag.Parse()
	report := func(message string) {
		if *stdio {
			_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"type": "fatal", "error": message})
		} else {
			showDesktopError(message)
		}
	}
	if err := os.MkdirAll(*dir, 0700); err != nil {
		if *watch == 0 {
			report("无法创建本机状态目录：" + err.Error())
		}
		return
	}
	if *watch != 0 {
		watchParent(*watch, *dir)
		return
	}
	unlock, err := lockDirectory(*dir)
	if err != nil {
		report("工具已运行，或状态目录无法锁定。请先退出旧版工具后重试。")
		return
	}
	defer unlock()
	if err := restoreProxy(*dir); err != nil {
		report("恢复代理失败：" + err.Error())
		return
	}
	if *restore {
		if *stdio {
			_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"type": "closed"})
		} else {
			showDesktopInfo("原代理配置已恢复。")
		}
		return
	}
	if !*stdio {
		showDesktopInfo("请打开 LynkSignTokenHelper-windows-x64.exe 使用桌面界面。")
		return
	}
	ca, certPath, err := loadCA(*dir)
	if err != nil {
		report("准备抓包证书失败：" + err.Error())
		return
	}
	app := newApp(*dir, certPath, ca)
	if err := runCore(app, os.Stdin, os.Stdout); err != nil {
		report(err.Error())
	}
}
