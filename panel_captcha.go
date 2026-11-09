package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"image/png"
	"net/url"
	"strings"
	"time"
)

type passwordChallenge struct {
	ID        string    `json:"id"`
	Image     string    `json:"image"`
	Address   string    `json:"address"`
	AllowHTTP bool      `json:"allow_http"`
	ExpiresAt time.Time `json:"expires_at"`
	panel     *panelClient
}

type panelRetryError struct {
	code, message string
	seconds       int
}

func (e *panelRetryError) Error() string { return e.message }

func panelUpstream() (*url.URL, error) {
	settings, err := readProxy()
	if err != nil {
		return nil, err
	}
	return settings.upstream()
}

func (p *panelClient) captcha(ctx context.Context, address string, allowHTTP bool) (*passwordChallenge, error) {
	var result struct {
		ID        string `json:"captcha_id"`
		Image     string `json:"image"`
		ExpiresIn int    `json:"expires_in"`
	}
	started := time.Now()
	if err := p.call(ctx, "/api/auth/captcha", nil, &result); err != nil {
		return nil, err
	}
	encoded, ok := strings.CutPrefix(result.Image, "data:image/png;base64,")
	if !ok || len(encoded) > 128<<10 || result.ID == "" || len(result.ID) > 128 || result.ExpiresIn < 1 {
		return nil, fmt.Errorf("验证码图片无效")
	}
	data, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return nil, fmt.Errorf("验证码图片无效")
	}
	image, err := png.DecodeConfig(bytes.NewReader(data))
	if err != nil || image.Width < 1 || image.Width > 512 || image.Height < 1 || image.Height > 256 {
		return nil, fmt.Errorf("验证码图片无效")
	}
	return &passwordChallenge{
		ID: result.ID, Image: encoded, Address: strings.TrimSpace(address), AllowHTTP: allowHTTP,
		ExpiresAt: started.Add(time.Duration(min(result.ExpiresIn, 180)) * time.Second), panel: p,
	}, nil
}

func validCaptchaCode(code string) bool {
	if len(code) != 4 {
		return false
	}
	for _, c := range code {
		if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') {
			return false
		}
	}
	return true
}

func (a *app) fetchLoginCaptcha(ctx context.Context, input actionInput) error {
	a.mu.Lock()
	a.status.LoginCaptcha = nil
	capturing := a.status.Capturing
	a.mu.Unlock()
	if capturing {
		return fmt.Errorf("请先停止抓包")
	}
	upstream, err := panelUpstream()
	if err != nil {
		return err
	}
	panel, err := newPanelClient(input.Address, input.AllowHTTP, upstream)
	if err != nil {
		return err
	}
	challenge, err := panel.captcha(ctx, input.Address, input.AllowHTTP)
	if err != nil {
		return err
	}
	a.mu.Lock()
	a.status.LoginCaptcha = challenge
	a.mu.Unlock()
	return nil
}
