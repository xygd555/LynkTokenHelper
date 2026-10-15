package main

import (
	"bytes"
	"compress/gzip"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func testCA(t *testing.T) *tls.Certificate {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	serial, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: "Isolated capture test"}, IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour)}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	leaf, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	return &tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key, Leaf: leaf}
}

func TestCapturePairsDeviceAndPreservesBodies(t *testing.T) {
	for _, test := range []struct{ body, content, device, token string }{
		{`{"hardwareDeviceId":"json-device"}`, "application/json", "json-device", ""},
		{`{"deviceId":"json-device","refreshToken":"body-refresh"}`, "application/json", "json-device", "body-refresh"},
		{"deviceId=form-device&refresh_token=form-refresh", "application/x-www-form-urlencoded", "form-device", "form-refresh"},
	} {
		t.Run(test.content+test.token, func(t *testing.T) {
			r := httptest.NewRequest("POST", "https://app.lynkco.com/login", strings.NewReader(test.body))
			r.Header.Set("Content-Type", test.content)
			values, device := requestCredentials(r)
			raw, _ := io.ReadAll(r.Body)
			if string(raw) != test.body || device != test.device {
				t.Fatal("request body or device lost")
			}
			if test.token != "" && (len(values) != 1 || values[0].Token != test.token || values[0].DeviceID != test.device) {
				t.Fatal("credential paired with wrong device")
			}
		})
	}
	var encoded bytes.Buffer
	writer := gzip.NewWriter(&encoded)
	_, _ = writer.Write([]byte(`{"data":{"refreshToken":"new-refresh"}}`))
	_ = writer.Close()
	original := append([]byte(nil), encoded.Bytes()...)
	response := &http.Response{StatusCode: 200, Header: http.Header{"Content-Encoding": {"gzip"}}, Body: io.NopCloser(bytes.NewReader(original))}
	values := responseCredentials(response, "same-device")
	raw, _ := io.ReadAll(response.Body)
	if !bytes.Equal(raw, original) || len(values) != 1 || values[0].Token != "new-refresh" || values[0].DeviceID != "same-device" {
		t.Fatal("response changed or credential missing")
	}
}

func TestCaptureDomainAndCredentialBoundaries(t *testing.T) {
	for host, want := range map[string]bool{"app.lynkco.com:443": true, "LYNKCO.COM.CN": true, "lynkco.com.attacker.test": false, "fake-lynkco.com": false, "localhost": false} {
		if lynkHost(host) != want {
			t.Fatalf("domain boundary: %s", host)
		}
	}
	var value any
	_ = json.Unmarshal([]byte(`{"data":[{"deviceId":"a","refreshToken":"one"},{"deviceId":"b","refresh_token":"two"}],"token":"access-only"}`), &value)
	values := uniqueCredentials(jsonCredentials(value, "", "response", 0))
	if len(values) != 2 {
		t.Fatal("access token matched or multiple credentials lost")
	}
	pairs := map[string]string{}
	for _, v := range values {
		pairs[v.Token] = v.DeviceID
	}
	if pairs["one"] != "a" || pairs["two"] != "b" {
		t.Fatal("cross-paired credentials")
	}
	r := httptest.NewRequest("POST", "https://app.lynkco.com/login", strings.NewReader(strings.Repeat("x", captureLimit+4)))
	if values, _ := requestCredentials(r); len(values) != 0 {
		t.Fatal("oversized body accepted")
	}
	raw, _ := io.ReadAll(r.Body)
	if len(raw) != captureLimit+4 {
		t.Fatal("oversized body truncated")
	}
}

func TestHTTPSProxyCapturesNewResponseTokenWithTLSVerification(t *testing.T) {
	ca := testCA(t)
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	leaf := &x509.Certificate{SerialNumber: big.NewInt(2), DNSNames: []string{"capture.lynkco.com"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, KeyUsage: x509.KeyUsageDigitalSignature}
	der, err := x509.CreateCertificate(rand.Reader, leaf, ca.Leaf, &key.PublicKey, ca.PrivateKey)
	if err != nil {
		t.Fatal(err)
	}
	originCert, err := tls.X509KeyPair(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
	if err != nil {
		t.Fatal(err)
	}
	origin := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("gl_dev_id") != "device-test" {
			t.Error("device header lost")
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"data":{"refreshToken":"new-token"}}`)
	}))
	origin.TLS = &tls.Config{Certificates: []tls.Certificate{originCert}}
	origin.StartTLS()
	defer origin.Close()
	captured := make(chan []credential, 2)
	proxy, err := startProxy(ca, nil, func(bool) {}, func(v []credential) { captured <- v }, func() { t.Error("proxy failed") })
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.close()
	pool := x509.NewCertPool()
	pool.AddCert(ca.Leaf)
	proxy.transport.TLSClientConfig.RootCAs = pool
	proxy.transport.DialContext = func(ctx context.Context, network, address string) (net.Conn, error) {
		var dialer net.Dialer
		return dialer.DialContext(ctx, network, origin.Listener.Addr().String())
	}
	proxyURL, _ := url.Parse("http://" + proxy.address())
	client := &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(proxyURL), TLSClientConfig: &tls.Config{RootCAs: pool}}, Timeout: 5 * time.Second}
	r, _ := http.NewRequest("GET", "https://capture.lynkco.com/login?refreshToken=old-token", nil)
	r.Header.Set("gl_dev_id", "device-test")
	response, err := client.Do(r)
	if err != nil {
		t.Fatal(err)
	}
	raw, _ := io.ReadAll(response.Body)
	_ = response.Body.Close()
	if !strings.Contains(string(raw), "new-token") {
		t.Fatal("upstream response lost")
	}
	select {
	case values := <-captured:
		if len(values) != 1 || values[0].Token != "new-token" || values[0].DeviceID != "device-test" {
			t.Fatalf("wrong capture: %#v", values)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("capture timed out")
	}
	// A missing upstream trust anchor must fail rather than disabling TLS checks.
	untrusted, err := startProxy(ca, nil, func(bool) {}, func([]credential) { t.Error("untrusted response captured") }, func() {})
	if err != nil {
		t.Fatal(err)
	}
	defer untrusted.close()
	untrusted.transport.DialContext = proxy.transport.DialContext
	untrustedURL, _ := url.Parse("http://" + untrusted.address())
	untrustedClient := &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(untrustedURL), TLSClientConfig: &tls.Config{RootCAs: pool}}, Timeout: 5 * time.Second}
	response, err = untrustedClient.Get("https://capture.lynkco.com/second")
	if err == nil {
		defer response.Body.Close()
		if response.StatusCode == 200 {
			t.Fatal("untrusted upstream accepted")
		}
	}
}
