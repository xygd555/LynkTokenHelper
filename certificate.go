package main

import (
	"crypto/rand"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"time"
)

func loadCA(dir string) (*tls.Certificate, string, error) {
	certPath, keyPath := filepath.Join(dir, "capture-ca.cer"), filepath.Join(dir, "capture-key.dpapi")
	certPEM, certErr := os.ReadFile(certPath)
	encrypted, keyErr := os.ReadFile(keyPath)
	if certErr == nil && keyErr == nil {
		keyPEM, err := unprotect(encrypted)
		if err != nil {
			return nil, "", fmt.Errorf("解密本机证书私钥失败")
		}
		cert, err := tls.X509KeyPair(certPEM, keyPEM)
		if err != nil {
			return nil, "", err
		}
		cert.Leaf, err = x509.ParseCertificate(cert.Certificate[0])
		if err != nil {
			return nil, "", err
		}
		if time.Now().After(cert.Leaf.NotAfter) {
			return nil, "", fmt.Errorf("本机抓包证书已过期，请移除旧证书并更换状态目录")
		}
		return &cert, certPath, nil
	}
	if !os.IsNotExist(certErr) || !os.IsNotExist(keyErr) {
		return nil, "", fmt.Errorf("本机证书文件不完整或无法读取，请更换状态目录")
	}
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, "", err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return nil, "", err
	}
	now := time.Now()
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: "Lynk Sign Token Helper " + fmt.Sprintf("%032x", serial)[:8]},
		NotBefore: now.Add(-time.Hour), NotAfter: now.AddDate(2, 0, 0), IsCA: true, BasicConstraintsValid: true,
		KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageCRLSign | x509.KeyUsageDigitalSignature}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return nil, "", err
	}
	certPEM = pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)})
	encrypted, err = protect(keyPEM)
	if err != nil {
		return nil, "", err
	}
	if err = os.WriteFile(keyPath, encrypted, 0600); err != nil {
		return nil, "", err
	}
	if err = os.WriteFile(certPath, certPEM, 0600); err != nil {
		return nil, "", err
	}
	cert, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		return nil, "", err
	}
	cert.Leaf, err = x509.ParseCertificate(der)
	return &cert, certPath, err
}
