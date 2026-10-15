package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"strconv"
	"time"
)

type coreRequest struct {
	Type     string      `json:"type"`
	ID       int64       `json:"id"`
	Action   string      `json:"action"`
	Input    actionInput `json:"input"`
	Selected int         `json:"selected"`
}

type coreSnapshot struct {
	Type          string    `json:"type"`
	Protocol      int       `json:"protocol"`
	State         appStatus `json:"state"`
	Token         string    `json:"token"`
	DeviceID      string    `json:"device_id"`
	DefaultServer string    `json:"default_server"`
	Selected      int       `json:"selected"`
	RequestID     int64     `json:"request_id"`
	Pending       bool      `json:"pending"`
	Closing       bool      `json:"closing"`
	Error         string    `json:"error"`
}

type coreResult struct {
	action string
	err    error
}

type coreSession struct {
	app        *app
	selected   int
	capturedAt time.Time
	requestID  int64
	pending    bool
	closing    bool
	lastError  string
}

func (s *coreSession) snapshot() coreSnapshot {
	s.app.mu.Lock()
	defer s.app.mu.Unlock()
	state := s.app.status
	state.Candidates = append([]candidateView(nil), state.Candidates...)
	if !state.CapturedAt.Equal(s.capturedAt) {
		s.capturedAt = state.CapturedAt
		s.selected = state.SelectedCandidate
	}
	if state.ConfirmationRequired {
		s.selected = state.SelectedCandidate
	}
	if s.selected < 0 || s.selected >= len(s.app.candidates) {
		s.selected = 0
	}
	var value credential
	if s.selected < len(s.app.candidates) {
		value = s.app.candidates[s.selected]
	}
	return coreSnapshot{Type: "snapshot", Protocol: 1, State: state, Token: value.Token, DeviceID: value.DeviceID, DefaultServer: defaultServer, Selected: s.selected, RequestID: s.requestID, Pending: s.pending, Closing: s.closing, Error: s.lastError}
}

// runCore only communicates through the anonymous pipes owned by the WPF host.
func runCore(app *app, input io.Reader, output io.Writer) error {
	session := &coreSession{app: app}
	requests := make(chan coreRequest)
	results := make(chan coreResult, 2)
	done := make(chan struct{})
	defer close(done)
	defer app.shutdown()
	go func() {
		defer close(requests)
		scanner := bufio.NewScanner(input)
		scanner.Buffer(make([]byte, 4096), 65536)
		for scanner.Scan() {
			var request coreRequest
			if json.Unmarshal(scanner.Bytes(), &request) != nil {
				continue
			}
			select {
			case requests <- request:
			case <-done:
				return
			}
		}
	}()
	encoder := json.NewEncoder(output)
	last := ""
	write := func() error {
		data := session.snapshot()
		raw, _ := json.Marshal(data)
		if string(raw) == last {
			return nil
		}
		last = string(raw)
		return encoder.Encode(data)
	}
	if err := write(); err != nil {
		return err
	}
	ticker := time.NewTicker(250 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case request, ok := <-requests:
			if !ok {
				return app.shutdown()
			}
			if request.Type == "close" {
				if !session.closing {
					session.closing = true
					go func() { results <- coreResult{"close", app.shutdown()} }()
				}
			} else if !session.closing && !session.pending && !app.currentStatus().Busy {
				switch request.Type {
				case "select":
					state := app.currentStatus()
					if !state.Capturing && !state.ConfirmationRequired && request.Selected >= 0 && request.Selected < len(state.Candidates) {
						session.selected = request.Selected
						session.lastError = ""
					}
				case "command":
					switch request.Action {
					case "login", "certificate/install", "certificate/remove", "start", "stop", "restore", "submit", "confirm", "cancel":
						state := app.currentStatus()
						if (request.Action == "submit" && (state.Capturing || state.ConfirmationRequired)) || (request.Action == "start" && state.ConfirmationRequired) {
							break
						}
						if request.Action == "submit" {
							request.Input.ID = strconv.Itoa(session.selected)
						}
						session.requestID = request.ID
						session.pending = true
						session.lastError = ""
						go func() {
							err := app.execute(app.context(), request.Action, request.Input)
							request.Input.Password = ""
							results <- coreResult{request.Action, err}
						}()
					}
				}
			}
		case result := <-results:
			if result.err != nil {
				session.lastError = result.err.Error()
			}
			if result.action == "close" {
				if result.err == nil {
					return encoder.Encode(struct {
						Type string `json:"type"`
					}{"closed"})
				}
				session.closing = false
			} else {
				session.pending = false
			}
		case <-ticker.C:
		}
		if err := write(); err != nil {
			return fmt.Errorf("桌面窗口连接已断开")
		}
	}
}
