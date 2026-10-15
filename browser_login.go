package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"time"
)

var authorizationValue = regexp.MustCompile(`^[A-Za-z0-9_-]{43}$`)
var errAuthorizationDenied = errors.New("网页授权已取消")

func browserRandomValue() (string, error) {
	var value [32]byte
	if _, err := rand.Read(value[:]); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(value[:]), nil
}

func loginPanelBrowser(ctx context.Context, address string, allowHTTP bool, upstream *url.URL, publish func(string)) (*panelClient, error) {
	return loginPanelBrowserWithTimeout(ctx, address, allowHTTP, upstream, publish, 5*time.Minute)
}

func loginPanelBrowserWithTimeout(parent context.Context, address string, allowHTTP bool, upstream *url.URL, publish func(string), lifetime time.Duration) (*panelClient, error) {
	panel, err := newPanelClient(address, allowHTTP, upstream)
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(parent, lifetime)
	defer cancel()
	verifier, err := browserRandomValue()
	if err != nil {
		return nil, fmt.Errorf("无法生成授权校验，请重试")
	}
	state, err := browserRandomValue()
	if err != nil {
		return nil, fmt.Errorf("无法生成授权校验，请重试")
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return nil, fmt.Errorf("无法接收浏览器授权，请检查本机端口权限后重试")
	}
	redirect := "http://" + listener.Addr().String() + "/helper/callback"
	type result struct {
		code   string
		denied bool
	}
	results := make(chan result, 1)
	mux := http.NewServeMux()
	mux.HandleFunc("GET /helper/callback", func(w http.ResponseWriter, r *http.Request) {
		values := r.URL.Query()
		if r.Host != listener.Addr().String() || subtle.ConstantTimeCompare([]byte(values.Get("state")), []byte(state)) != 1 ||
			(values.Get("error") != "access_denied" && !authorizationValue.MatchString(values.Get("code"))) {
			http.Error(w, "授权校验失败，请回到工具重新发起授权。", 400)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'")
		_, _ = w.Write([]byte(`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>授权结果已交回工具</title><style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#202329;color:#f2f4f8;font:15px/1.7 "Microsoft YaHei UI","Segoe UI",sans-serif}main{max-width:380px;margin:24px;padding:32px;background:#2b3038;border:1px solid #ffffff1a;border-radius:18px}h1{font-size:23px;margin:0 0 12px}p{color:#b2bbc9;margin:0}</style><main><h1>授权结果已交回工具</h1><p>请返回领克凭据助手查看登录状态。此页面可以关闭。</p></main></html>`))
		select {
		case results <- result{values.Get("code"), values.Get("error") == "access_denied"}:
		default:
		}
	})
	server := &http.Server{Handler: mux, ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 10 * time.Second, MaxHeaderBytes: 8192}
	serverErrors := make(chan error, 1)
	go func() { serverErrors <- server.Serve(listener) }()
	defer func() {
		shutdown, stop := context.WithTimeout(context.Background(), time.Second)
		defer stop()
		_ = server.Shutdown(shutdown)
		_ = listener.Close()
	}()
	digest := sha256.Sum256([]byte(verifier))
	var request struct {
		ID string `json:"request_id"`
	}
	if err := panel.call(ctx, "/api/helper/authorizations", map[string]string{"redirect_uri": redirect, "code_challenge": base64.RawURLEncoding.EncodeToString(digest[:]), "code_challenge_method": "S256", "state": state}, &request); err != nil {
		return nil, err
	}
	if !authorizationValue.MatchString(request.ID) {
		return nil, fmt.Errorf("面板返回的授权请求无效，请更新面板后重试")
	}
	completed := false
	defer func() {
		if !completed {
			cleanup, stop := context.WithTimeout(context.Background(), 2*time.Second)
			defer stop()
			var response struct {
				OK bool `json:"ok"`
			}
			_ = panel.call(cleanup, "/api/helper/authorizations/"+request.ID+"/cancel", map[string]string{"code_verifier": verifier}, &response)
		}
	}()
	publish(panel.base + "/helper/authorize?request_id=" + request.ID)
	select {
	case result := <-results:
		if result.denied {
			return nil, errAuthorizationDenied
		}
		var token struct {
			AccessToken string `json:"access_token"`
			TokenType   string `json:"token_type"`
		}
		if err := panel.call(ctx, "/api/helper/token", map[string]string{"grant_type": "authorization_code", "request_id": request.ID, "code": result.code, "code_verifier": verifier, "redirect_uri": redirect}, &token); err != nil {
			return nil, err
		}
		if token.AccessToken == "" || token.TokenType != "Bearer" {
			return nil, fmt.Errorf("面板未返回有效的工具授权凭证")
		}
		panel.accessToken = token.AccessToken
		if err := panel.checkIdentity(ctx); err != nil {
			panel.revokeHelperSession()
			return nil, err
		}
		completed = true
		return panel, nil
	case <-ctx.Done():
		if errors.Is(ctx.Err(), context.DeadlineExceeded) {
			return nil, fmt.Errorf("网页授权等待已超时，请重新发起授权")
		}
		return nil, ctx.Err()
	case <-serverErrors:
		return nil, fmt.Errorf("浏览器授权接收已中断，请重新发起授权")
	}
}

func (p *panelClient) revokeHelperSession() {
	if p == nil || p.accessToken == "" {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	var response struct {
		OK bool `json:"ok"`
	}
	_ = p.call(ctx, "/api/auth/logout", map[string]string{}, &response)
	p.accessToken = ""
}
