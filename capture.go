package main

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
)

const captureLimit = 2 << 20

type credential struct{ Token, DeviceID, Source string }

func lynkHost(host string) bool {
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}
	host = strings.TrimSuffix(strings.ToLower(host), ".")
	for _, domain := range []string{"lynkco.com", "lynkco.com.cn"} {
		if host == domain || strings.HasSuffix(host, "."+domain) {
			return true
		}
	}
	return false
}

func deviceName(name string) bool {
	switch strings.ToLower(name) {
	case "deviceid", "device_id", "hardwaredeviceid", "gl_dev_id":
		return true
	}
	return false
}
func tokenName(name string) bool {
	name = strings.ToLower(name)
	return name == "refreshtoken" || name == "refresh_token"
}

func jsonDevices(value any, depth int) []string {
	if depth > 24 {
		return nil
	}
	var result []string
	switch object := value.(type) {
	case map[string]any:
		for key, value := range object {
			if text, ok := value.(string); ok && deviceName(key) {
				result = append(result, strings.TrimSpace(text))
			} else {
				result = append(result, jsonDevices(value, depth+1)...)
			}
		}
	case []any:
		for _, value := range object {
			result = append(result, jsonDevices(value, depth+1)...)
		}
	}
	return result
}

func singleDevice(values []string) string {
	device := ""
	for _, value := range values {
		if value == "" {
			continue
		}
		if device != "" && device != value {
			return ""
		}
		device = value
	}
	return device
}

func jsonCredentials(value any, fallback, source string, depth int) []credential {
	if depth > 24 {
		return nil
	}
	var result []credential
	switch object := value.(type) {
	case map[string]any:
		device := fallback
		var local []string
		for key, value := range object {
			if text, ok := value.(string); ok && deviceName(key) {
				local = append(local, strings.TrimSpace(text))
			}
		}
		if len(local) > 0 {
			device = singleDevice(local)
		}
		for key, value := range object {
			if tokenName(key) {
				if text, ok := value.(string); ok && strings.TrimSpace(text) != "" {
					result = append(result, credential{strings.TrimSpace(text), device, source})
				}
			} else {
				result = append(result, jsonCredentials(value, device, source, depth+1)...)
			}
		}
	case []any:
		for _, value := range object {
			result = append(result, jsonCredentials(value, fallback, source, depth+1)...)
		}
	}
	return result
}

type replayBody struct {
	io.Reader
	io.Closer
}

func peekBody(body *io.ReadCloser) []byte {
	if body == nil || *body == nil {
		return nil
	}
	original := *body
	prefix, err := io.ReadAll(io.LimitReader(original, captureLimit+1))
	*body = replayBody{io.MultiReader(bytes.NewReader(prefix), original), original}
	if err != nil || len(prefix) > captureLimit {
		return nil
	}
	return prefix
}

func requestCredentials(r *http.Request) ([]credential, string) {
	device := strings.TrimSpace(r.Header.Get("gl_dev_id"))
	if device == "" {
		var devices []string
		for key, values := range r.URL.Query() {
			if deviceName(key) {
				devices = append(devices, values...)
			}
		}
		for key, values := range r.Header {
			if deviceName(key) {
				devices = append(devices, values...)
			}
		}
		device = singleDevice(devices)
	}
	var result []credential
	addPairs := func(values url.Values, source string) {
		local := device
		if local == "" {
			var devices []string
			for key, values := range values {
				if deviceName(key) {
					devices = append(devices, values...)
				}
			}
			local = singleDevice(devices)
		}
		if device == "" {
			device = local
		}
		for key, values := range values {
			if tokenName(key) {
				for _, token := range values {
					if token = strings.TrimSpace(token); token != "" {
						result = append(result, credential{token, local, source})
					}
				}
			}
		}
	}
	addPairs(r.URL.Query(), "请求参数")
	for key, values := range r.Header {
		if tokenName(key) {
			for _, token := range values {
				result = append(result, credential{strings.TrimSpace(token), device, "请求头"})
			}
		}
	}
	if r.Body != nil {
		raw := peekBody(&r.Body)
		var value any
		if json.Unmarshal(raw, &value) == nil {
			if device == "" {
				device = singleDevice(jsonDevices(value, 0))
			}
			result = append(result, jsonCredentials(value, device, "请求正文", 0)...)
		} else if strings.HasPrefix(r.Header.Get("Content-Type"), "application/x-www-form-urlencoded") {
			values, err := url.ParseQuery(string(raw))
			if err == nil {
				addPairs(values, "请求表单")
			}
		}
	}
	return result, device
}

func responseCredentials(response *http.Response, device string) []credential {
	if response == nil || response.StatusCode < 200 || response.StatusCode >= 300 || response.Body == nil {
		return nil
	}
	contentType := strings.ToLower(response.Header.Get("Content-Type"))
	if contentType != "" && !strings.Contains(contentType, "json") && !strings.HasPrefix(contentType, "text/plain") {
		return nil
	}
	raw := peekBody(&response.Body)
	if strings.EqualFold(response.Header.Get("Content-Encoding"), "gzip") {
		reader, err := gzip.NewReader(bytes.NewReader(raw))
		if err != nil {
			return nil
		}
		defer reader.Close()
		raw, err = io.ReadAll(io.LimitReader(reader, captureLimit+1))
		if err != nil || len(raw) > captureLimit {
			return nil
		}
	} else if encoding := response.Header.Get("Content-Encoding"); encoding != "" && encoding != "identity" {
		return nil
	}
	var value any
	if json.Unmarshal(raw, &value) != nil {
		return nil
	}
	return jsonCredentials(value, device, "响应正文", 0)
}

func uniqueCredentials(values []credential) []credential {
	seen := map[string]bool{}
	var result []credential
	for _, value := range values {
		if value.Token == "" || len(value.Token) > 16384 {
			continue
		}
		key := value.Token + "\x00" + value.DeviceID
		if !seen[key] {
			seen[key] = true
			result = append(result, value)
		}
	}
	return result
}
