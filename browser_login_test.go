package main

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestBrowserLoginPKCECallbackAndCleanup(t *testing.T) {
	for _, scenario := range []string{"success", "wrong-state", "denied", "cancelled", "timeout", "membership-expired", "unsupported"} {
		t.Run(scenario, func(t *testing.T) {
			var redirect, state, challenge string
			var metadataMu sync.RWMutex
			var cancelled, revoked atomic.Int32
			requestID, code := strings.Repeat("r", 43), strings.Repeat("c", 43)
			token := "synthetic-helper-session"
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				switch r.URL.Path {
				case "/api/helper/authorizations":
					if scenario == "unsupported" {
						w.WriteHeader(404)
						return
					}
					var input map[string]string
					_ = json.NewDecoder(r.Body).Decode(&input)
					metadataMu.Lock()
					redirect, state, challenge = input["redirect_uri"], input["state"], input["code_challenge"]
					metadataMu.Unlock()
					if input["code_challenge_method"] != "S256" || input["password"] != "" || input["username"] != "" {
						t.Error("browser login sent password or omitted PKCE")
					}
					_, _ = io.WriteString(w, `{"request_id":"`+requestID+`"}`)
				case "/api/helper/token":
					var input map[string]string
					_ = json.NewDecoder(r.Body).Decode(&input)
					digest := sha256.Sum256([]byte(input["code_verifier"]))
					metadataMu.RLock()
					if input["code"] != code || input["request_id"] != requestID || input["redirect_uri"] != redirect || base64.RawURLEncoding.EncodeToString(digest[:]) != challenge {
						t.Error("code exchange lost its proof or callback binding")
					}
					metadataMu.RUnlock()
					_, _ = io.WriteString(w, `{"access_token":"`+token+`","token_type":"Bearer"}`)
				case "/api/auth/me":
					if r.Header.Get("Authorization") != "Bearer "+token {
						t.Error("helper identity lookup omitted bearer")
					}
					active := scenario != "membership-expired"
					_ = json.NewEncoder(w).Encode(map[string]any{"username": "测试用户", "entitlement": map[string]bool{"active": active}})
				case "/api/account/import":
					if r.Header.Get("Authorization") != "Bearer "+token {
						t.Error("credential import omitted helper bearer")
					}
					_, _ = io.WriteString(w, `{"ok":true}`)
				case "/api/auth/logout":
					revoked.Add(1)
					_, _ = io.WriteString(w, `{"ok":true}`)
				case "/api/helper/authorizations/" + requestID + "/cancel":
					cancelled.Add(1)
					_, _ = io.WriteString(w, `{"ok":true}`)
				default:
					w.WriteHeader(404)
				}
			}))
			defer server.Close()
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			timeout := 2 * time.Second
			if scenario == "timeout" {
				timeout = 80 * time.Millisecond
			}
			callbackDone := make(chan struct{}, 1)
			panel, err := loginPanelBrowserWithTimeout(ctx, server.URL, true, nil, func(address string) {
				if address != server.URL+"/helper/authorize?request_id="+requestID {
					t.Error("authorization browser left the configured server")
				}
				if scenario == "cancelled" {
					cancel()
					return
				}
				if scenario == "timeout" {
					return
				}
				metadataMu.RLock()
				callbackRedirect, callbackState := redirect, state
				metadataMu.RUnlock()
				go func() {
					defer func() { callbackDone <- struct{}{} }()
					u, _ := url.Parse(callbackRedirect)
					values := url.Values{"state": {callbackState}, "code": {code}}
					if scenario == "wrong-state" {
						values.Set("state", "invalid")
						u.RawQuery = values.Encode()
						response, e := http.Get(u.String())
						if e != nil {
							t.Error(e)
							return
						}
						response.Body.Close()
						if response.StatusCode != 400 {
							t.Error("unbound callback accepted")
						}
						values.Set("state", callbackState)
					}
					if scenario == "denied" {
						values.Del("code")
						values.Set("error", "access_denied")
					}
					u.RawQuery = values.Encode()
					response, e := http.Get(u.String())
					if e != nil {
						t.Error(e)
						return
					}
					body, _ := io.ReadAll(response.Body)
					response.Body.Close()
					if response.StatusCode != 200 || !strings.Contains(string(body), "授权结果已交回工具") {
						t.Error("callback completion page unavailable")
					}
				}()
			}, timeout)
			success := scenario == "success" || scenario == "wrong-state"
			if (err == nil) != success {
				t.Fatalf("unexpected login result: %v", err)
			}
			if success {
				if panel.username != "测试用户" || panel.accessToken != token {
					t.Fatal("browser approval did not create a logged in panel client")
				}
				var result importResult
				if err := panel.call(ctx, "/api/account/import", map[string]string{"refresh_token": "synthetic"}, &result); err != nil || !result.OK {
					t.Fatal("authorized import failed")
				}
				panel.revokeHelperSession()
				if revoked.Load() != 1 {
					t.Fatal("helper session was not revoked on release")
				}
			}
			if scenario == "success" || scenario == "wrong-state" || scenario == "denied" || scenario == "membership-expired" {
				select {
				case <-callbackDone:
				case <-time.After(time.Second):
					t.Fatal("callback page not completed")
				}
			}
			if scenario != "unsupported" {
				metadataMu.RLock()
				callbackRedirect := redirect
				metadataMu.RUnlock()
				u, _ := url.Parse(callbackRedirect)
				conn, e := net.DialTimeout("tcp", u.Host, 100*time.Millisecond)
				if e == nil {
					conn.Close()
					t.Fatal("authorization listener stayed open")
				}
			}
			if !success && scenario != "unsupported" && cancelled.Load() != 1 {
				t.Fatal("abandoned authorization request not cancelled")
			}
			if scenario == "membership-expired" && revoked.Load() != 1 {
				t.Fatal("unusable helper session not revoked")
			}
		})
	}
}

func TestCorePipeBrowserLoginCanCancel(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/helper/authorizations" {
			_, _ = io.WriteString(w, `{"request_id":"`+strings.Repeat("r", 43)+`"}`)
		} else {
			_, _ = io.WriteString(w, `{"ok":true}`)
		}
	}))
	defer server.Close()
	app := newApp(t.TempDir(), "", testCA(t))
	reader, writer := io.Pipe()
	outputReader, output := io.Pipe()
	defer reader.Close()
	defer writer.Close()
	defer outputReader.Close()
	defer output.Close()
	done := make(chan error, 1)
	go func() { done <- runCore(app, reader, output) }()
	encoder, decoder := json.NewEncoder(writer), json.NewDecoder(outputReader)
	read := func(match func(coreSnapshot) bool) coreSnapshot {
		t.Helper()
		for {
			var value coreSnapshot
			if err := decoder.Decode(&value); err != nil {
				t.Fatal(err)
			}
			if match(value) {
				return value
			}
		}
	}
	read(func(s coreSnapshot) bool { return s.Type == "snapshot" })
	_ = encoder.Encode(coreRequest{Type: "command", ID: 1, Action: "login/browser", Input: actionInput{Address: server.URL, AllowHTTP: true}})
	read(func(s coreSnapshot) bool { return s.State.Authorizing && s.State.AuthorizationURL != "" })
	_ = encoder.Encode(coreRequest{Type: "command", ID: 2, Action: "login/cancel"})
	result := read(func(s coreSnapshot) bool { return s.RequestID == 1 && !s.Pending })
	if result.Error != "" || result.State.LoggedIn || result.State.Authorizing || result.State.AuthorizationURL != "" || !strings.Contains(result.State.Message, "已取消") {
		t.Fatal("cancelled browser login did not return to a usable login state")
	}
	_ = encoder.Encode(coreRequest{Type: "close"})
	read(func(s coreSnapshot) bool { return s.Type == "closed" })
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}
