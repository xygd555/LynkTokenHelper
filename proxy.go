package main

import (
	"crypto/tls"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"sync"
	"time"

	"github.com/elazarl/goproxy"
)

type captureProxy struct {
	listener  *trackedListener
	server    *http.Server
	transport *http.Transport
}
type requestCapture struct {
	candidates []credential
	device     string
}

func startProxy(ca *tls.Certificate, upstream *url.URL, activity func(bool), captured func([]credential), failed func()) (*captureProxy, error) {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	tracked := &trackedListener{Listener: listener, connections: make(map[*trackedConn]bool)}
	proxy := goproxy.NewProxyHttpServer()
	proxy.Logger = log.New(io.Discard, "", 0)
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = nil
	transport.TLSClientConfig = &tls.Config{MinVersion: tls.VersionTLS12}
	transport.ResponseHeaderTimeout = 30 * time.Second
	if upstream != nil {
		transport.Proxy = http.ProxyURL(upstream)
		proxy.ConnectDial = proxy.NewConnectDialToProxy(upstream.String())
	} else {
		proxy.ConnectDial = nil
	}
	proxy.Tr = transport
	action := &goproxy.ConnectAction{Action: goproxy.ConnectMitm, TLSConfig: goproxy.TLSConfigFromCA(ca)}
	proxy.OnRequest().HandleConnectFunc(func(host string, ctx *goproxy.ProxyCtx) (*goproxy.ConnectAction, string) {
		activity(lynkHost(host))
		if lynkHost(host) {
			return action, host
		}
		return goproxy.OkConnect, host
	})
	proxy.OnRequest().DoFunc(func(r *http.Request, ctx *goproxy.ProxyCtx) (*http.Request, *http.Response) {
		if lynkHost(r.URL.Hostname()) {
			activity(true)
			candidates, device := requestCredentials(r)
			ctx.UserData = requestCapture{candidates, device}
			r.Header.Set("Accept-Encoding", "gzip")
		}
		return r, nil
	})
	proxy.OnResponse().DoFunc(func(response *http.Response, ctx *goproxy.ProxyCtx) *http.Response {
		capture, ok := ctx.UserData.(requestCapture)
		if !ok {
			return response
		}
		if response == nil {
			failed()
			return response
		}
		values := responseCredentials(response, capture.device)
		if len(values) == 0 {
			values = capture.candidates
		}
		if values = uniqueCredentials(values); len(values) > 0 {
			go captured(values)
		}
		return response
	})
	server := &http.Server{Handler: proxy, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 60 * time.Second}
	result := &captureProxy{tracked, server, transport}
	go func() { _ = server.Serve(tracked) }()
	return result, nil
}

func (p *captureProxy) address() string { return p.listener.Addr().String() }
func (p *captureProxy) close() {
	_ = p.server.Close()
	p.transport.CloseIdleConnections()
	p.listener.mu.Lock()
	connections := make([]*trackedConn, 0, len(p.listener.connections))
	for conn := range p.listener.connections {
		connections = append(connections, conn)
	}
	p.listener.mu.Unlock()
	for _, conn := range connections {
		_ = conn.Close()
	}
}

type trackedListener struct {
	net.Listener
	mu          sync.Mutex
	connections map[*trackedConn]bool
}
type trackedConn struct {
	net.Conn
	owner *trackedListener
}

func (l *trackedListener) Accept() (net.Conn, error) {
	conn, err := l.Listener.Accept()
	if err != nil {
		return nil, err
	}
	tracked := &trackedConn{conn, l}
	l.mu.Lock()
	l.connections[tracked] = true
	l.mu.Unlock()
	return tracked, nil
}
func (c *trackedConn) Close() error {
	c.owner.mu.Lock()
	delete(c.owner.connections, c)
	c.owner.mu.Unlock()
	return c.Conn.Close()
}
