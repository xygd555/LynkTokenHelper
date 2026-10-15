package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"strings"
	"time"
)

type panelClient struct {
	base           string
	client         *http.Client
	csrf, username string
	accessToken    string
}
type accountInfo struct {
	ID          string `json:"id"`
	DisplayName string `json:"display_name"`
}
type importResult struct {
	OK                   bool        `json:"ok"`
	ConfirmationRequired bool        `json:"confirmation_required"`
	ConfirmationID       string      `json:"confirmation_id"`
	Account              accountInfo `json:"account"`
	PreviousAccountID    string      `json:"previous_account_id"`
}

func newPanelClient(address string, allowHTTP bool, upstream *url.URL) (*panelClient, error) {
	u, err := url.Parse(strings.TrimRight(strings.TrimSpace(address), "/"))
	if err != nil || u.Hostname() == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Scheme != "https" && u.Scheme != "http") {
		return nil, fmt.Errorf("请填写完整的 HTTP 或 HTTPS 服务器地址")
	}
	if u.Scheme == "http" && !allowHTTP {
		return nil, fmt.Errorf("HTTP 会明文传输账号和凭据，请使用 HTTPS，或勾选内网 HTTP 测试")
	}
	jar, _ := cookiejar.New(nil)
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = nil
	if upstream != nil {
		transport.Proxy = http.ProxyURL(upstream)
	}
	client := &http.Client{Jar: jar, Transport: transport, Timeout: 70 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	return &panelClient{base: strings.TrimRight(u.String(), "/"), client: client}, nil
}

func loginPanel(ctx context.Context, address, username, password string, allowHTTP bool, upstream *url.URL) (*panelClient, error) {
	panel, err := newPanelClient(address, allowHTTP, upstream)
	if err != nil {
		return nil, err
	}
	var login struct {
		CSRF       string `json:"csrf_token"`
		MustChange bool   `json:"must_change_password"`
	}
	if err := panel.call(ctx, "/api/auth/login", map[string]string{"username": username, "password": password}, &login); err != nil {
		return nil, err
	}
	panel.csrf = login.CSRF
	if panel.csrf == "" {
		return nil, fmt.Errorf("服务器未返回登录会话，请检查地址是否指向 Lynk Sign")
	}
	if login.MustChange {
		return nil, fmt.Errorf("请先在面板修改初始密码，再登录工具")
	}
	if err := panel.checkIdentity(ctx); err != nil {
		return nil, err
	}
	return panel, nil
}

func (panel *panelClient) checkIdentity(ctx context.Context) error {
	var me struct {
		Username    string `json:"username"`
		Entitlement struct {
			Active bool `json:"active"`
		} `json:"entitlement"`
	}
	if err := panel.call(ctx, "/api/auth/me", nil, &me); err != nil {
		return err
	}
	if !me.Entitlement.Active {
		return fmt.Errorf("账号权益已到期，请先在面板续期")
	}
	panel.username = me.Username
	return nil
}

func (p *panelClient) call(ctx context.Context, path string, input, output any) error {
	method := http.MethodGet
	var body io.Reader
	if input != nil {
		method = http.MethodPost
		raw, err := json.Marshal(input)
		if err != nil {
			return err
		}
		body = bytes.NewReader(raw)
	}
	r, err := http.NewRequestWithContext(ctx, method, p.base+path, body)
	if err != nil {
		return fmt.Errorf("服务器地址无效")
	}
	r.Header.Set("Content-Type", "application/json")
	if p.accessToken != "" {
		r.Header.Set("Authorization", "Bearer "+p.accessToken)
	}
	if p.csrf != "" && p.accessToken == "" {
		r.Header.Set("X-CSRF-Token", p.csrf)
	}
	response, err := p.client.Do(r)
	if err != nil {
		return fmt.Errorf("无法连接服务器，请检查地址、网络和 HTTPS 证书")
	}
	defer response.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	if err != nil {
		return fmt.Errorf("读取服务器响应失败")
	}
	if response.StatusCode != 200 {
		// Only expose known error codes; remote messages may contain credentials.
		var failure struct {
			Code string `json:"code"`
		}
		_ = json.Unmarshal(raw, &failure)
		messages := map[string]string{"auth_required": "面板登录已过期，请重新登录", "membership_expired": "账号权益已到期", "account_verify_failed": "领克凭据验证失败，请重新登录小程序后再试", "account_changed": "服务器账号配置已变更，请重新抓取", "confirmation_expired": "换绑确认已过期，请重新抓取", "csrf_failed": "登录校验已过期，请重新登录", "invalid_grant": "网页授权已过期或校验失败，请重新授权", "authorization_expired": "网页授权已过期，请重新授权", "authorization_rate_limited": "网页授权请求过于频繁，请稍后重试", "helper_scope_denied": "工具授权权限不足，请重新授权"}
		if message := messages[failure.Code]; message != "" {
			return fmt.Errorf("%s", message)
		}
		if response.StatusCode == 404 && strings.HasPrefix(path, "/api/helper/") {
			return fmt.Errorf("网页授权需要面板 v1.4.0 或更高版本，请更新面板或使用账号密码登录")
		}
		if response.StatusCode == 404 {
			return fmt.Errorf("服务器接口不存在，请先更新服务端至 v1.1.0 或更高版本")
		}
		if path == "/api/auth/login" && (response.StatusCode == 401 || response.StatusCode == 403) {
			return fmt.Errorf("账号密码错误或账号已停用")
		}
		return fmt.Errorf("服务器返回 HTTP %d，请检查面板状态", response.StatusCode)
	}
	if err := json.Unmarshal(raw, output); err != nil {
		return fmt.Errorf("服务器响应格式无效")
	}
	return nil
}
