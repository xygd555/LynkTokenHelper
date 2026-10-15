package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
)

func TestShutdownRejectsQueuedOperations(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	app := &app{dir: t.TempDir(), ctx: ctx, cancel: cancel, status: appStatus{Message: "original state"}}
	if err := app.shutdown(); err != nil {
		t.Fatal(err)
	}
	if err := app.execute(context.Background(), "cancel", actionInput{}); err == nil {
		t.Fatal("queued operation ran after window shutdown")
	}
	if app.currentStatus().Message != "original state" {
		t.Fatal("queued operation modified state after shutdown")
	}
}

func TestCapturedCredentialSurvivesSubmitFailureConfirmationAndCancel(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch calls.Add(1) {
		case 1:
			w.WriteHeader(http.StatusServiceUnavailable)
		case 2, 3:
			_, _ = w.Write([]byte(`{"ok":true,"confirmation_required":true,"confirmation_id":"pending-capture","account":{"id":"next-account","display_name":"测试领克"},"previous_account_id":"old-account"}`))
		default:
			_, _ = w.Write([]byte(`{"ok":true,"account":{"id":"next-account","display_name":"测试领克"}}`))
		}
	}))
	defer server.Close()
	app := newApp(t.TempDir(), "", testCA(t))
	defer app.cancel()
	retained := func() credential {
		app.mu.Lock()
		defer app.mu.Unlock()
		if len(app.candidates) != 1 {
			return credential{}
		}
		return app.candidates[0]
	}
	app.panel = &panelClient{base: server.URL, client: server.Client()}
	app.status.Capturing = true
	app.generation = 5
	value := credential{"rt_synthetic_backup_value", "synthetic-device", "响应正文"}
	app.receive(5, []credential{value})
	if state := app.currentStatus(); state.Capturing || state.Submission != "failed" || len(state.Candidates) != 1 || state.CapturedAt.IsZero() {
		t.Fatal("failed automatic submission did not preserve capture")
	}
	if got := retained(); got != value {
		t.Fatal("automatic submission lost backup credential")
	}
	for index, action := range []string{"submit", "cancel", "submit", "confirm"} {
		if err := app.execute(context.Background(), action, actionInput{ID: strconv.Itoa(0)}); err != nil {
			t.Fatalf("step %d failed: %v", index, err)
		}
		if retained() != value {
			t.Fatalf("step %d discarded backup credential", index)
		}
	}
	if state := app.currentStatus(); state.Submission != "saved" || state.ConfirmationRequired || len(state.Candidates) != 1 {
		t.Fatal("saved credential state incorrect")
	}
	data, _ := json.Marshal(app.currentStatus())
	if strings.Contains(string(data), value.Token) {
		t.Fatal("raw credential leaked into serialized status")
	}
	_ = app.execute(context.Background(), "login", actionInput{Address: "invalid", Username: "synthetic", Password: "synthetic"})
	if retained().Token != "" || len(app.currentStatus().Candidates) != 0 {
		t.Fatal("server switch retained previous server credential")
	}
}
