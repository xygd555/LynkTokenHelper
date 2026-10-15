package main

import (
	"bytes"
	"testing"
)

func TestWindowsReadProxyWithoutMutation(t *testing.T) {
	if _, err := readProxy(); err != nil {
		t.Fatal(err)
	}
}
func TestWindowsCertificateKeyIsProtectedAndDirectoryLocked(t *testing.T) {
	dir := t.TempDir()
	ca, path, err := loadCA(dir)
	if err != nil {
		t.Fatal(err)
	}
	loaded, again, err := loadCA(dir)
	if err != nil || path != again || !bytes.Equal(ca.Certificate[0], loaded.Certificate[0]) {
		t.Fatal("protected CA failed round trip")
	}
	if certificateTrusted(ca) {
		t.Fatal("creating CA installed trust without user action")
	}
	unlock, err := lockDirectory(dir)
	if err != nil {
		t.Fatal(err)
	}
	if second, err := lockDirectory(dir); err == nil {
		second()
		t.Fatal("second process acquired directory lock")
	}
	unlock()
	third, err := lockDirectory(dir)
	if err != nil {
		t.Fatal(err)
	}
	third()
}
