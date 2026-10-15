package main

import (
	"context"
	"crypto/tls"
	"fmt"
	"strconv"
	"sync"
	"time"
)

type candidateView struct {
	ID       string `json:"id"`
	DeviceID string `json:"device_id"`
	Source   string `json:"source"`
}
type appStatus struct {
	Message              string          `json:"message"`
	LoggedIn             bool            `json:"logged_in"`
	Username             string          `json:"username"`
	ServerAddress        string          `json:"server_address"`
	Trusted              bool            `json:"trusted"`
	Capturing            bool            `json:"capturing"`
	Connections          int             `json:"connections"`
	LynkRequests         int             `json:"lynk_requests"`
	Candidates           []candidateView `json:"candidates"`
	ConfirmationRequired bool            `json:"confirmation_required"`
	Account              accountInfo     `json:"account"`
	PreviousAccountID    string          `json:"previous_account_id"`
	Busy                 bool            `json:"busy"`
	Submission           string          `json:"submission"`
	SelectedCandidate    int             `json:"selected_candidate"`
	CapturedAt           time.Time       `json:"captured_at"`
}

type app struct {
	mu             sync.Mutex
	operations     sync.Mutex
	dir, certPath  string
	ca             *tls.Certificate
	panel          *panelClient
	proxy          *captureProxy
	status         appStatus
	candidates     []credential
	confirmationID string
	generation     int
	timer          *time.Timer
	watching       bool
	closing        bool
	ctx            context.Context
	cancel         context.CancelFunc
}

func newApp(dir, certPath string, ca *tls.Certificate) *app {
	ctx, cancel := context.WithCancel(context.Background())
	return &app{dir: dir, certPath: certPath, ca: ca, ctx: ctx, cancel: cancel, status: appStatus{Message: "请先登录服务器，再安装证书并开始获取。", Trusted: certificateTrusted(ca)}}
}

type actionInput struct {
	Address, Username, Password string
	AllowHTTP                   bool
	ID                          string
}

func (a *app) currentStatus() appStatus {
	a.mu.Lock()
	defer a.mu.Unlock()
	state := a.status
	state.Candidates = append([]candidateView(nil), state.Candidates...)
	return state
}

func (a *app) context() context.Context {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.ctx == nil {
		return context.Background()
	}
	return a.ctx
}

func (a *app) execute(ctx context.Context, action string, input actionInput) error {
	if !a.operations.TryLock() {
		return fmt.Errorf("已有操作正在处理，请稍后重试")
	}
	defer a.operations.Unlock()
	a.mu.Lock()
	if a.closing {
		a.mu.Unlock()
		return fmt.Errorf("工具正在退出，请重新打开后操作")
	}
	a.status.Busy = true
	a.mu.Unlock()
	defer func() { a.mu.Lock(); a.status.Busy = false; a.mu.Unlock() }()
	var err error
	switch action {
	case "login":
		if err = a.stopCapture(false); err != nil {
			break
		}
		a.mu.Lock()
		a.panel = nil
		a.status.LoggedIn = false
		a.status.Username = ""
		a.status.ServerAddress = ""
		a.candidates = nil
		a.confirmationID = ""
		a.status.Candidates = nil
		a.status.ConfirmationRequired = false
		a.status.Submission = ""
		a.status.CapturedAt = time.Time{}
		a.status.SelectedCandidate = 0
		a.status.Account = accountInfo{}
		a.mu.Unlock()
		settings, readErr := readProxy()
		if readErr != nil {
			err = readErr
			break
		}
		upstream, proxyErr := settings.upstream()
		if proxyErr != nil {
			err = proxyErr
			break
		}
		panel, loginErr := loginPanel(ctx, input.Address, input.Username, input.Password, input.AllowHTTP, upstream)
		input.Password = ""
		if loginErr != nil {
			err = loginErr
			break
		}
		a.mu.Lock()
		a.panel = panel
		a.status.LoggedIn = true
		a.status.Username = panel.username
		a.status.ServerAddress = panel.base
		a.status.Message = "服务器登录成功，可以开始获取。"
		a.mu.Unlock()
	case "certificate/install":
		err = installCertificate(a.certPath)
		if err == nil {
			a.mu.Lock()
			a.status.Trusted = certificateTrusted(a.ca)
			a.status.Message = "本机证书已安装。"
			a.mu.Unlock()
		}
	case "certificate/remove":
		if err = a.stopCapture(false); err != nil {
			break
		}
		err = removeCertificate(a.ca)
		if err == nil {
			a.mu.Lock()
			a.status.Trusted = false
			a.status.Message = "本工具的抓包证书已移除。"
			a.mu.Unlock()
		}
	case "start":
		err = a.startCapture()
	case "stop", "restore":
		err = a.stopCapture(false)
		if err == nil {
			a.mu.Lock()
			a.status.Message = "已停止获取，原代理配置已恢复。"
			a.mu.Unlock()
		}
	case "submit":
		index, parseErr := strconv.Atoi(input.ID)
		a.mu.Lock()
		var value credential
		if parseErr == nil && index >= 0 && index < len(a.candidates) {
			value = a.candidates[index]
			a.status.SelectedCandidate = index
		}
		a.mu.Unlock()
		if value.Token == "" {
			err = fmt.Errorf("候选凭据已失效，请重新获取")
			break
		}
		err = a.submit(ctx, map[string]string{"refresh_token": value.Token, "device_id": value.DeviceID})
	case "confirm":
		a.mu.Lock()
		id := a.confirmationID
		a.mu.Unlock()
		if id == "" {
			err = fmt.Errorf("没有待确认的换绑")
			break
		}
		err = a.submit(ctx, map[string]string{"confirmation_id": id})
	case "cancel":
		a.mu.Lock()
		a.confirmationID = ""
		a.status.ConfirmationRequired = false
		a.status.Submission = "cancelled"
		a.status.Message = "已取消换绑，原账号保持不变。"
		a.mu.Unlock()
	default:
		err = fmt.Errorf("未知操作")
	}
	if err != nil {
		a.mu.Lock()
		a.status.Message = err.Error()
		a.mu.Unlock()
	}
	return err
}

func (a *app) shutdown() error {
	a.mu.Lock()
	a.closing = true
	cancel := a.cancel
	a.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	a.operations.Lock()
	defer a.operations.Unlock()
	err := a.stopCapture(false)
	if err != nil {
		a.mu.Lock()
		a.closing = false
		a.ctx, a.cancel = context.WithCancel(context.Background())
		a.status.Message = err.Error()
		a.mu.Unlock()
	}
	return err
}

func (a *app) startCapture() error {
	if err := a.stopCapture(false); err != nil {
		return err
	}
	a.mu.Lock()
	loggedIn := a.panel != nil
	a.mu.Unlock()
	if !loggedIn {
		return fmt.Errorf("请先登录服务器")
	}
	if !certificateTrusted(a.ca) {
		return fmt.Errorf("请先点击“安装抓包证书”")
	}
	settings, err := readProxy()
	if err != nil {
		return err
	}
	upstream, err := settings.upstream()
	if err != nil {
		return err
	}
	if !a.watching {
		if err := startWatchdog(a.dir); err != nil {
			return err
		}
		a.watching = true
	}
	a.mu.Lock()
	a.generation++
	generation := a.generation
	a.mu.Unlock()
	proxy, err := startProxy(a.ca, upstream, func(lynk bool) {
		a.mu.Lock()
		defer a.mu.Unlock()
		if a.generation == generation && a.status.Capturing {
			a.status.Connections++
			if lynk {
				a.status.LynkRequests++
			}
		}
	}, func(values []credential) { a.receive(generation, values) }, func() {
		a.mu.Lock()
		defer a.mu.Unlock()
		if a.generation == generation && a.status.Capturing {
			a.status.Message = "领克连接失败，请检查证书信任，并重新打开小程序。"
		}
	})
	if err != nil {
		return fmt.Errorf("启动本地代理失败")
	}
	if err = activateProxy(a.dir, settings, proxy.address()); err != nil {
		proxy.close()
		return err
	}
	a.mu.Lock()
	a.proxy = proxy
	a.confirmationID = ""
	a.status.ConfirmationRequired = false
	a.status.Submission = ""
	a.status.Capturing = true
	a.status.Connections = 0
	a.status.LynkRequests = 0
	a.status.Account = accountInfo{}
	a.status.Message = "等待领克登录流量，请点击工具里的“打开领克小程序”。未获取到凭据时，请登录或退出账号后重新登录。"
	a.mu.Unlock()
	a.timer = time.AfterFunc(10*time.Minute, func() {
		a.operations.Lock()
		defer a.operations.Unlock()
		a.mu.Lock()
		same := a.generation == generation && a.status.Capturing
		a.mu.Unlock()
		if same {
			err := a.stopCapture(false)
			a.mu.Lock()
			if err != nil {
				a.status.Message = err.Error()
			} else {
				a.status.Message = "等待已超时，代理已恢复；可以重新开始获取。"
			}
			a.mu.Unlock()
		}
	})
	return nil
}

func (a *app) stopCapture(graceful bool) error {
	if err := restoreProxy(a.dir); err != nil {
		return err
	}
	if a.timer != nil {
		a.timer.Stop()
		a.timer = nil
	}
	a.mu.Lock()
	proxy := a.proxy
	a.proxy = nil
	a.status.Capturing = false
	a.generation++
	a.mu.Unlock()
	if proxy != nil {
		if graceful {
			time.AfterFunc(2*time.Second, proxy.close)
		} else {
			proxy.close()
		}
	}
	return nil
}

func (a *app) receive(generation int, values []credential) {
	a.operations.Lock()
	defer a.operations.Unlock()
	a.mu.Lock()
	valid := a.status.Capturing && a.generation == generation && !a.closing
	a.mu.Unlock()
	if !valid {
		return
	}
	if err := a.stopCapture(true); err != nil {
		a.mu.Lock()
		a.status.Message = err.Error()
		a.mu.Unlock()
		return
	}
	a.mu.Lock()
	a.candidates = values
	a.status.CapturedAt = time.Now()
	a.status.SelectedCandidate = 0
	a.status.Submission = ""
	a.status.Candidates = nil
	for index, value := range values {
		a.status.Candidates = append(a.status.Candidates, candidateView{strconv.Itoa(index), value.DeviceID, value.Source})
	}
	a.status.Message = "已捕获凭据，正在提交服务器验证。"
	a.mu.Unlock()
	if len(values) == 1 {
		ctx, cancel := context.WithTimeout(a.context(), 65*time.Second)
		defer cancel()
		if err := a.submit(ctx, map[string]string{"refresh_token": values[0].Token, "device_id": values[0].DeviceID}); err != nil {
			a.mu.Lock()
			a.status.Message = err.Error() + "；可以重试提交或重新获取。"
			a.mu.Unlock()
		}
	} else {
		a.mu.Lock()
		a.status.Message = "捕获到多个候选凭据，请选择后提交。"
		a.mu.Unlock()
	}
}

func (a *app) submit(ctx context.Context, input any) (err error) {
	a.mu.Lock()
	panel := a.panel
	a.status.Busy = true
	a.status.Submission = "submitting"
	a.mu.Unlock()
	defer func() {
		a.mu.Lock()
		a.status.Busy = false
		if err != nil {
			a.status.Submission = "failed"
		}
		a.mu.Unlock()
	}()
	if panel == nil {
		return fmt.Errorf("请先登录服务器")
	}
	var result importResult
	if err = panel.call(ctx, "/api/account/import", input, &result); err != nil {
		return err
	}
	if !result.OK || result.Account.ID == "" || (result.ConfirmationRequired && result.ConfirmationID == "") {
		return fmt.Errorf("服务器未返回有效的账号导入结果")
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	a.status.Account = result.Account
	a.status.ConfirmationRequired = result.ConfirmationRequired
	a.status.PreviousAccountID = result.PreviousAccountID
	a.confirmationID = result.ConfirmationID
	if result.ConfirmationRequired {
		a.status.Submission = "confirmation"
		a.status.Message = "检测到账号变更，请核对领克账号并确认换绑。"
	} else {
		a.status.Submission = "saved"
		a.confirmationID = ""
		a.status.Message = "领克账号凭据已验证并保存成功。"
	}
	return nil
}
