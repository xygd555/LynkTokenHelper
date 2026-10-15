# Windows 领克凭据助手

在 Windows 电脑的微信中登录领克小程序，获取登录凭证并自动提交到 Lynk Sign 面板。工具使用原生 WPF 界面，无需安装 Go、浏览器内核或新版 .NET Desktop Runtime。

支持 Windows 10 1903 及以后版本、Windows 11 的 64 位系统，使用系统自带的 .NET Framework 4.8 或兼容版本。目前尚未完成 Windows 10 实机验收。

## 下载与使用

下载 [最新版 EXE](https://github.com/xygd555/LynkTokenHelper/releases/latest/download/LynkSignTokenHelper-windows-x64.exe)，或下载包含说明和许可证的 [ZIP 包](https://github.com/xygd555/LynkTokenHelper/releases/latest/download/LynkSignTokenHelper-windows-x64.zip)。

1. 确认 Lynk Sign 面板为 **v1.1.0 或更高版本**，工具需要服务端的凭据导入接口。
2. 双击 `LynkSignTokenHelper-windows-x64.exe`。填写**面板地址、面板账号和密码**，点击“登录服务器”；这里填写的是面板账号，不是领克手机号。公网使用 HTTPS，内网 HTTP 测试需手动勾选明文传输选项。
3. 首次使用，点击“抓包证书”旁的“安装”，证书安装在当前 Windows 用户中。
4. 点击“开始抓取”，在**同一台电脑的微信**中打开领克小程序并重新登录。没有捕获到时，先关闭小程序再打开。
5. 捕获单个凭据后自动提交并验证。多个凭据需选择；与面板原绑定账号不同或旧账号身份未知时，核对后确认换绑。验证失败会保留面板原凭据，可重试提交或重新抓取。
6. 工具提示保存成功后，回到面板刷新页面即可，**无需再导入 HAR 文件或手动粘贴凭证**。
7. 完成后关闭工具。工具会先取消请求并恢复原代理；不再使用时，可点击“移除证书”。

主界面可查看并复制本次捕获的 Token 和设备 ID；保存成功、提交失败或取消换绑后，本次捕获值仍保留。重新抓取时，收到新凭据后才替换；切换服务器重新登录会清除之前捕获的凭据。

## 面板下载按钮

在 Lynk Sign 面板部署环境中配置：

```env
LYNK_HELPER_REPOSITORY=https://github.com/xygd555/LynkTokenHelper
```

仓库地址也支持 `.git` 后缀。面板中的“下载凭证工具”按钮会使用最新版的固定附件地址；发布新版本后无需修改面板链接。容器修改环境变量后需重新创建，单独重启不会加载新值。

## 抓包与恢复

- 内置 Go HTTPS 代理，仅监听 `127.0.0.1`。只解密 `lynkco.com`、`lynkco.com.cn` 及其子域，其他 HTTPS 连接透传。
- 开始抓取前备份 Windows 原代理；完成、取消、超时或退出时恢复。主进程异常退出时，由独立守护进程尝试恢复，下一次启动也会检查。若用户或其他软件修改了代理，则保留其新设置。
- 停电或强制结束所有进程后，可重新运行工具恢复，也可执行 `LynkSignTokenHelper-windows-x64.exe --restore-proxy`。仍无法恢复时，在 Windows“设置 → 网络和 Internet → 代理”中检查手动代理。
- 支持直连和常见 HTTP 手动上游代理。PAC、仅 SOCKS 上游代理、HTTP 与 HTTPS 分别使用不同代理暂不支持，会在修改设置前提示。
- 微信是否使用系统代理、是否信任当前用户证书，需要在具体版本中实测。获取等待时间为 10 分钟；连接计数不代表已获得有效凭据。
- 工具不保存密码、面板会话、领克 Token 或完整抓包文件。本次凭据只在内存中保留，复制内容由 Windows 剪贴板管理。
- 每个状态目录生成独有 CA，私钥由当前 Windows 用户的 DPAPI 加密。证书、代理恢复信息及内嵌内核缓存放在 EXE 旁的 `state/`，抓取期间不要移动或删除。
- EXE 内嵌 WPF 界面和 Go 抓包内核，通过匿名管道通信。工具尚未进行代码签名。

## 本地构建与验证

开发环境需要 Windows、Go（版本见 `go.mod`）、Visual Studio 的 .NET 桌面开发工作负载和 .NET Framework 4.8 开发工具。

```powershell
./build.ps1
./test.ps1
```

默认服务器地址可在构建时预填，软件中仍可修改：

```powershell
./build.ps1 -DefaultServer 'https://sign.example.com'
```

输出为 `dist/LynkSignTokenHelper-windows-x64.exe` 和同名 ZIP。ZIP 包含 EXE、使用说明及许可证。图标、源码和构建资源全部位于本仓库，**构建不依赖面板源码目录**。

`test.ps1` 执行 Go 测试、静态检查和 WPF 交互测试。测试使用虚构凭据、模拟面板及隔离状态目录，不安装证书或启用系统代理。测试截图输出到 `test-results/wpf-native/`；不同渲染比例截图不等同于多机 DPI 实机验收。

## 发布新版

工具使用根目录 `VERSION` 中的独立版本号，格式为 `v1.3.6`，修改工具后先更新该文件。

1. 修改工具后更新 `VERSION`，提交代码并推送到 `main` 分支。
2. **推送后自动构建并发布**，发布说明使用此次推送最后一个提交的说明，正式版会设为 **Latest**。
3. 如果 `VERSION` 对应的版本已经发布，本次仍会构建并保存 Actions 产物，但跳过重复发布；需要发布新版时先递增 `VERSION`。

仍可在 **Actions → 构建并发布 Windows 凭据助手 → Run workflow** 手动运行，填写自定义发布说明，可选勾选“标记为预发布版本”。预发布版不会替换正式版下载入口。

工作流执行 Go 测试与静态检查、构建 WPF 程序、核对 EXE 文件版本并发布 Release。Release 标签为 `VERSION` 中的版本号，附件始终使用：

- `LynkSignTokenHelper-windows-x64.exe`
- `LynkSignTokenHelper-windows-x64.zip`

每个版本的 EXE 文件属性和工具界面都会显示对应版本号，固定附件名用于保持下载链接稳定。已发布附件不会被覆盖；自动运行遇到同名 Release 会跳过发布，手动重复发布同名版本仍会报错。

可在 **Settings → Secrets and variables → Actions → Variables** 配置 `WINDOWS_HELPER_DEFAULT_SERVER`，用于编译时预填面板地址。它与面板运行环境中的 `LYNK_HELPER_REPOSITORY` 是两个独立配置。

工作流使用内置 `GITHUB_TOKEN`，无需另建 PAT；本地完整 WPF 交互验证仍应在发布前执行。

## 源码与许可证

`wpf/` 维护原生界面和内核通信；Go 文件维护抓包、凭据提取、证书、面板接口及代理恢复。界面设计记录位于 `design/DESIGN.md`。

本项目采用 [MIT License](LICENSE)，第三方许可证见 `licenses/`。`dist/`、`.cache/`、`test-results/`、`state/` 和 WPF 构建目录不提交 Git。
