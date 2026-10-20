package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestLocalCapturePreservesCandidatesAndRejectsEveryUploadPath(t *testing.T) {
	for _, count := range []int{1, 2} {
		name := "single"
		if count == 2 {
			name = "multiple"
		}
		t.Run(name, func(t *testing.T) {
			var calls atomic.Int32
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				w.WriteHeader(http.StatusInternalServerError)
			}))
			defer server.Close()
			app := newApp(t.TempDir(), "", testCA(t))
			defer app.cancel()
			// Retain a stale panel to prove that the mode, rather than a nil client, blocks uploads.
			app.panel = &panelClient{base: server.URL, client: server.Client()}
			app.status.LocalOnly = true
			app.status.Capturing = true
			app.generation = 5
			values := []credential{
				{"rt_first_synthetic_local", "first-device", "响应正文"},
				{"rt_second_synthetic_local", "second-device", "请求正文"},
			}[:count]
			app.receive(5, values)
			state := app.currentStatus()
			if state.Capturing || !state.LocalOnly || state.Busy || state.Submission != "" || len(state.Candidates) != count || state.CapturedAt.IsZero() {
				t.Fatal("local capture did not preserve a completed local-only state")
			}
			if !reflect.DeepEqual(app.candidates, values) {
				t.Fatal("local capture changed or discarded credentials")
			}
			raw, _ := json.Marshal(state)
			if strings.Contains(string(raw), "rt_") {
				t.Fatal("local status leaked raw credentials")
			}
			session := &coreSession{app: app, capturedAt: state.CapturedAt}
			for index, expected := range values {
				session.selected = index
				snapshot := session.snapshot()
				if snapshot.Token != expected.Token || snapshot.DeviceID != expected.DeviceID || snapshot.Selected != index {
					t.Fatal("local pipe snapshot lost a selected credential")
				}
			}
			app.confirmationID = "stale-confirmation"
			for _, action := range []string{"submit", "confirm"} {
				if err := app.execute(context.Background(), action, actionInput{ID: "1"}); err == nil || !strings.Contains(err.Error(), "本地抓包模式") {
					t.Fatalf("local mode accepted %s", action)
				}
			}
			if err := app.submit(context.Background(), map[string]string{"refresh_token": values[0].Token}); err == nil || !strings.Contains(err.Error(), "本地抓包模式") {
				t.Fatal("direct submission bypassed local-only mode")
			}
			if calls.Load() != 0 {
				t.Fatalf("local capture made %d server requests", calls.Load())
			}
			if state := app.currentStatus(); state.Busy || state.Submission != "" || state.SelectedCandidate != 0 || !reflect.DeepEqual(app.candidates, values) {
				t.Fatal("rejected upload changed the retained local capture")
			}
		})
	}
}

func TestCaptureLocalClearsServerStateWithoutContactingPanel(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusInternalServerError)
	}))
	defer server.Close()
	app := newApp(t.TempDir(), "", testCA(t))
	defer app.cancel()
	app.panel = &panelClient{base: server.URL, client: server.Client(), accessToken: "synthetic-helper-session"}
	app.candidates = []credential{{"rt_previous_synthetic", "previous-device", "响应正文"}}
	app.confirmationID = "previous-confirmation"
	app.status = appStatus{
		LoggedIn: true, Username: "previous-user", ServerAddress: server.URL,
		Authorizing: true, AuthorizationURL: server.URL + "/authorize",
		Capturing: true, Candidates: []candidateView{{"0", "previous-device", "响应正文"}},
		ConfirmationRequired: true, PreviousAccountID: "old-account",
		Account: accountInfo{ID: "new-account"}, Submission: "confirmation",
		SelectedCandidate: 1, CapturedAt: time.Now(),
	}
	if err := app.execute(context.Background(), "capture/local", actionInput{}); err != nil {
		t.Fatal(err)
	}
	state := app.currentStatus()
	if !state.LocalOnly || state.LoggedIn || state.Authorizing || state.AuthorizationURL != "" || state.Username != "" || state.ServerAddress != "" || state.Capturing || state.Busy {
		t.Fatal("local entry retained an authenticated server state")
	}
	if app.panel != nil || len(app.candidates) != 0 || len(state.Candidates) != 0 || app.confirmationID != "" || state.ConfirmationRequired || state.Account.ID != "" || state.PreviousAccountID != "" || state.Submission != "" || state.SelectedCandidate != 0 || !state.CapturedAt.IsZero() {
		t.Fatal("local entry retained an earlier capture or binding")
	}
	if calls.Load() != 0 {
		t.Fatal("entering local mode contacted the former panel")
	}
	app.candidates = []credential{{"rt_local_synthetic", "local-device", "响应正文"}}
	app.status.Candidates = []candidateView{{"0", "local-device", "响应正文"}}
	app.status.CapturedAt = time.Now()
	// A failed login must still leave local mode and discard its retained capture.
	_ = app.execute(context.Background(), "login", actionInput{Address: "invalid", Username: "synthetic", Password: "synthetic"})
	state = app.currentStatus()
	if state.LocalOnly || state.LoggedIn || app.panel != nil || len(app.candidates) != 0 || len(state.Candidates) != 0 || !state.CapturedAt.IsZero() {
		t.Fatal("server login retained local-mode credentials")
	}
	if calls.Load() != 0 {
		t.Fatal("mode switch submitted a retained credential")
	}
}

func TestLocalCaptureStartRequiresCertificateInsteadOfServerLogin(t *testing.T) {
	app := newApp(t.TempDir(), "", testCA(t))
	defer app.cancel()
	if err := app.startCapture(); err == nil || err.Error() != "请先登录服务器" {
		t.Fatal("server capture no longer requires login")
	}
	if err := app.execute(context.Background(), "capture/local", actionInput{}); err != nil {
		t.Fatal(err)
	}
	// The isolated test CA is never installed in the user's trust store.
	if err := app.startCapture(); err == nil || err.Error() != "请先点击“安装抓包证书”" {
		t.Fatalf("local capture did not proceed to the certificate requirement: %v", err)
	}
	if app.currentStatus().Capturing {
		t.Fatal("an untrusted test certificate started capture")
	}
}

func TestCorePipeLocalModeSelectsCredentialsAndRejectsUpload(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusInternalServerError)
	}))
	defer server.Close()
	app := newApp(t.TempDir(), "", testCA(t))
	app.panel = &panelClient{base: server.URL, client: server.Client(), accessToken: "synthetic-helper-session"}
	app.status.LoggedIn = true
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
			var snapshot coreSnapshot
			if err := decoder.Decode(&snapshot); err != nil {
				t.Fatal(err)
			}
			if match(snapshot) {
				return snapshot
			}
		}
	}
	read(func(s coreSnapshot) bool { return s.Type == "snapshot" })
	_ = encoder.Encode(coreRequest{Type: "command", ID: 1, Action: "capture/local"})
	entered := read(func(s coreSnapshot) bool { return s.RequestID == 1 && !s.Pending })
	if !entered.State.LocalOnly || entered.State.LoggedIn || entered.Error != "" {
		t.Fatal("pipe did not enter unauthenticated local mode")
	}
	app.mu.Lock()
	app.status.Capturing = true
	app.generation = 10
	app.mu.Unlock()
	app.receive(10, []credential{
		{"rt_first_synthetic_local", "first-device", "响应正文"},
		{"rt_second_synthetic_local", "second-device", "响应正文"},
	})
	read(func(s coreSnapshot) bool { return len(s.State.Candidates) == 2 })
	_ = encoder.Encode(coreRequest{Type: "select", Selected: 1})
	selected := read(func(s coreSnapshot) bool { return s.Selected == 1 })
	if selected.Token != "rt_second_synthetic_local" || selected.DeviceID != "second-device" || !selected.State.LocalOnly {
		t.Fatal("local pipe did not expose the selected credential for copying")
	}
	for index, action := range []string{"submit", "confirm"} {
		id := int64(index + 2)
		_ = encoder.Encode(coreRequest{Type: "command", ID: id, Action: action})
		rejected := read(func(s coreSnapshot) bool { return s.RequestID == id && !s.Pending })
		if !strings.Contains(rejected.Error, "本地抓包模式") || rejected.Token != selected.Token || rejected.State.Submission != "" {
			t.Fatalf("pipe command %s bypassed local-only capture", action)
		}
	}
	_ = encoder.Encode(coreRequest{Type: "close"})
	read(func(s coreSnapshot) bool { return s.Type == "closed" })
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 0 {
		t.Fatalf("local pipe made %d server requests", calls.Load())
	}
}
