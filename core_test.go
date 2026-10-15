package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestCorePipeLoginSelectSubmitAndClose(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/auth/login":
			var input map[string]string
			_ = json.NewDecoder(r.Body).Decode(&input)
			if input["username"] != "测试用户" || input["password"] != "synthetic-password" {
				t.Error("pipe login payload changed")
			}
			http.SetCookie(w, &http.Cookie{Name: "session", Value: "synthetic", Path: "/"})
			_, _ = w.Write([]byte(`{"csrf_token":"synthetic-csrf"}`))
		case "/api/auth/me":
			_, _ = w.Write([]byte(`{"username":"测试用户","entitlement":{"active":true}}`))
		case "/api/account/import":
			if r.Header.Get("X-CSRF-Token") != "synthetic-csrf" {
				t.Error("pipe import lost CSRF")
			}
			var input map[string]string
			_ = json.NewDecoder(r.Body).Decode(&input)
			if input["refresh_token"] != "rt_second_synthetic" {
				t.Error("pipe selection was not submitted")
			}
			_, _ = w.Write([]byte(`{"ok":true,"account":{"id":"synthetic-account"}}`))
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
	read(func(s coreSnapshot) bool { return s.Type == "snapshot" && s.Protocol == 1 })
	_ = encoder.Encode(coreRequest{Type: "command", ID: 1, Action: "login", Input: actionInput{Address: server.URL, Username: "测试用户", Password: "synthetic-password", AllowHTTP: true}})
	read(func(s coreSnapshot) bool { return s.RequestID == 1 && !s.Pending && s.State.LoggedIn })
	app.mu.Lock()
	app.candidates = []credential{{"rt_first_synthetic", "one", "响应正文"}, {"rt_second_synthetic", "two", "响应正文"}}
	app.status.Candidates = []candidateView{{"0", "one", "响应正文"}, {"1", "two", "响应正文"}}
	app.status.CapturedAt = time.Now()
	app.mu.Unlock()
	read(func(s coreSnapshot) bool { return len(s.State.Candidates) == 2 })
	_ = encoder.Encode(coreRequest{Type: "select", Selected: 1})
	selected := read(func(s coreSnapshot) bool { return s.Selected == 1 })
	if selected.Token != "rt_second_synthetic" || selected.DeviceID != "two" {
		t.Fatal("pipe snapshot lost full credential")
	}
	_ = encoder.Encode(coreRequest{Type: "command", ID: 2, Action: "submit"})
	saved := read(func(s coreSnapshot) bool { return !s.Pending && s.State.Submission == "saved" })
	if saved.Token != selected.Token {
		t.Fatal("pipe save lost backup credential")
	}
	_ = encoder.Encode(coreRequest{Type: "close"})
	read(func(s coreSnapshot) bool { return s.Type == "closed" })
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestCorePipeCloseCancelsOperation(t *testing.T) {
	for _, mode := range []string{"message", "EOF"} {
		t.Run(mode, func(t *testing.T) {
			started := make(chan struct{})
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				_, _ = io.Copy(io.Discard, r.Body)
				close(started)
				<-r.Context().Done()
			}))
			defer server.Close()
			app := newApp(t.TempDir(), "", testCA(t))
			reader, writer := io.Pipe()
			defer reader.Close()
			done := make(chan error, 1)
			go func() { done <- runCore(app, reader, io.Discard) }()
			_ = json.NewEncoder(writer).Encode(coreRequest{Type: "command", ID: 1, Action: "login", Input: actionInput{Address: server.URL, Username: "synthetic", Password: "synthetic", AllowHTTP: true}})
			select {
			case <-started:
			case <-time.After(5 * time.Second):
				t.Fatal("pipe command did not start")
			}
			if mode == "EOF" {
				_ = writer.Close()
			} else {
				_ = json.NewEncoder(writer).Encode(coreRequest{Type: "close"})
				defer writer.Close()
			}
			select {
			case err := <-done:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(5 * time.Second):
				t.Fatal("desktop EOF did not cancel and exit")
			}
			if app.context().Err() != context.Canceled {
				t.Fatal("desktop EOF left operation context active")
			}
		})
	}
}
