package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestPanelLoginAndImportUseSessionAndCSRF(t *testing.T) {
	imports := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/auth/login":
			var input map[string]string
			_ = json.NewDecoder(r.Body).Decode(&input)
			if input["username"] != "tester" || input["password"] != "test-password" {
				t.Error("login payload lost")
			}
			http.SetCookie(w, &http.Cookie{Name: "lynk_session", Value: "session-test", Path: "/", HttpOnly: true})
			_, _ = w.Write([]byte(`{"csrf_token":"csrf-test"}`))
		case "/api/auth/me":
			cookie, err := r.Cookie("lynk_session")
			if err != nil || cookie.Value != "session-test" {
				t.Error("missing session")
			}
			_, _ = w.Write([]byte(`{"username":"tester","entitlement":{"active":true}}`))
		case "/api/account/import":
			imports++
			if r.Header.Get("X-CSRF-Token") != "csrf-test" {
				t.Error("missing CSRF")
			}
			cookie, err := r.Cookie("lynk_session")
			if err != nil || cookie.Value != "session-test" {
				t.Error("import missing session")
			}
			var input map[string]string
			_ = json.NewDecoder(r.Body).Decode(&input)
			if input["refresh_token"] != "captured-test" || input["device_id"] != "device-test" {
				t.Error("wrong credential payload")
			}
			_, _ = w.Write([]byte(`{"ok":true,"account":{"id":"200","display_name":"模拟领克"}}`))
		default:
			t.Errorf("unexpected endpoint %s", r.URL.Path)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	if _, err := loginPanel(context.Background(), server.URL, "tester", "test-password", false, nil); err == nil {
		t.Fatal("HTTP silently accepted")
	}
	panel, err := loginPanel(context.Background(), server.URL, "tester", "test-password", true, nil)
	if err != nil {
		t.Fatal(err)
	}
	app := &app{panel: panel}
	if err := app.submit(context.Background(), map[string]string{"refresh_token": "captured-test", "device_id": "device-test"}); err != nil {
		t.Fatal(err)
	}
	if imports != 1 || app.status.Account.ID != "200" || app.status.ConfirmationRequired {
		t.Fatal("import result lost")
	}
	state, _ := json.Marshal(app.status)
	if strings.Contains(string(state), "captured-test") || strings.Contains(string(state), "session-test") {
		t.Fatal("local status leaked secrets")
	}
}

func TestPanelRedirectAndRemoteErrorCannotLeakCredentials(t *testing.T) {
	received := false
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { received = true }))
	defer target.Close()
	source := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { http.Redirect(w, r, target.URL, 307) }))
	defer source.Close()
	if _, err := loginPanel(context.Background(), source.URL, "tester", "private-password", true, nil); err == nil || strings.Contains(err.Error(), "private-password") {
		t.Fatal("redirect accepted or error leaked")
	}
	if received {
		t.Fatal("credentials forwarded to redirect")
	}
	failure := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(502)
		_, _ = w.Write([]byte(`{"error":"private-refresh private-password"}`))
	}))
	defer failure.Close()
	if _, err := loginPanel(context.Background(), failure.URL, "tester", "private-password", true, nil); err == nil || strings.Contains(err.Error(), "private-") {
		t.Fatal("remote message leaked")
	}
}
