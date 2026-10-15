package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"testing"
)

func TestExistingProxySelection(t *testing.T) {
	for _, test := range []struct {
		settings proxySettings
		want     string
		failure  bool
	}{
		{proxySettings{Flags: 1}, "", false},
		{proxySettings{Flags: 3, Server: "127.0.0.1:7890"}, "http://127.0.0.1:7890", false},
		{proxySettings{Flags: 3, Server: "http=127.0.0.1:7890;https=127.0.0.1:7890"}, "http://127.0.0.1:7890", false},
		{proxySettings{Flags: 5, PAC: "https://example.test/proxy.pac"}, "", true},
		{proxySettings{Flags: 3, Server: "socks=127.0.0.1:1080"}, "", true},
		{proxySettings{Flags: 3, Server: "http=127.0.0.1:7890;https=127.0.0.1:7891"}, "", true},
	} {
		proxy, err := test.settings.upstream()
		if (err != nil) != test.failure {
			t.Fatalf("unexpected proxy error: %v", err)
		}
		got := ""
		if proxy != nil {
			got = proxy.String()
		}
		if got != test.want {
			t.Fatalf("upstream %q want %q", got, test.want)
		}
	}
}

func TestProxyRecoveryPreservesExternalChangesAndRetryRecord(t *testing.T) {
	for _, scenario := range []string{"owned", "external change", "write failure"} {
		t.Run(scenario, func(t *testing.T) {
			dir := t.TempDir()
			backup := proxyBackup{Before: proxySettings{Flags: 9, Bypass: "<local>"}, Installed: proxySettings{Flags: 3, Server: "http=127.0.0.1:1234;https=127.0.0.1:1234"}}
			raw, _ := json.Marshal(backup)
			path := filepath.Join(dir, "proxy-backup.json")
			if err := os.WriteFile(path, raw, 0600); err != nil {
				t.Fatal(err)
			}
			current := backup.Installed
			if scenario == "external change" {
				current = proxySettings{Flags: 3, Server: "127.0.0.1:7890"}
			}
			writes := 0
			err := restoreProxyUsing(dir, func() (proxySettings, error) { return current, nil }, func(settings proxySettings) error {
				writes++
				if settings != backup.Before {
					t.Fatal("restored wrong settings")
				}
				if scenario == "write failure" {
					return fmt.Errorf("test failure")
				}
				return nil
			})
			if scenario == "write failure" {
				if err == nil {
					t.Fatal("restore failure hidden")
				}
				if _, err := os.Stat(path); err != nil {
					t.Fatal("retry record lost")
				}
			} else {
				if err != nil {
					t.Fatal(err)
				}
				if _, err := os.Stat(path); !os.IsNotExist(err) {
					t.Fatal("recovery record not cleaned")
				}
				if scenario == "external change" && writes != 0 {
					t.Fatal("overwrote another application's proxy")
				}
				if scenario == "owned" && writes != 1 {
					t.Fatal("owned proxy was not restored")
				}
			}
		})
	}
}
