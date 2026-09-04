// Package hosting：ServerHost.cs（证书加载半边）→ cert.go。
// PEM 全支持（同名 .key 边车查找）；PFX/P12 用 go-pkcs12（无密码，Q10）。
// Go 私钥仅在内存中，不含 C# 的 Windows DefaultKeySet/EphemeralKeySet 平台分支（§3.3）。
package hosting

import (
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"software.sslmate.com/src/go-pkcs12"
)

// LoadCertificate 对应 C# CertificateLoader.Load：扩展名分发。
func LoadCertificate(path string) (tls.Certificate, error) {
	if strings.TrimSpace(path) == "" {
		return tls.Certificate{}, errors.New("server.certificate_path must not be empty.")
	}

	if _, err := os.Stat(path); err != nil {
		if os.IsNotExist(err) {
			return tls.Certificate{}, fmt.Errorf("TLS certificate file was not found.")
		}
		return tls.Certificate{}, err
	}

	lower := strings.ToLower(path)
	switch {
	case strings.HasSuffix(lower, ".pem") || strings.HasSuffix(lower, ".crt"):
		return loadPEM(path)
	case strings.HasSuffix(lower, ".pfx") || strings.HasSuffix(lower, ".p12"):
		return loadPKCS12(path)
	default:
		return tls.Certificate{}, errors.New("TLS certificate must use .pem, .crt, .pfx, or .p12 extension.")
	}
}

// loadPEM 对应 C# LoadPemCertificate：同名 .key 边车查找、bundle 解析、
// 叶证书 + 私钥匹配；缺私钥/无证书错误文案一致。
func loadPEM(certificatePath string) (tls.Certificate, error) {
	keyPath := certificatePath
	if _, err := os.Stat(strings.TrimSuffix(certificatePath, filepath.Ext(certificatePath)) + ".key"); err == nil {
		keyPath = strings.TrimSuffix(certificatePath, filepath.Ext(certificatePath)) + ".key"
	}

	certificates, err := parseCertificatesFromPEMFile(certificatePath)
	if err != nil {
		return tls.Certificate{}, fmt.Errorf("Unable to load PEM certificate '%s': %v", certificatePath, err)
	}
	if len(certificates) == 0 {
		return tls.Certificate{}, fmt.Errorf("Unable to load PEM certificate '%s': PEM file does not contain a certificate.", certificatePath)
	}

	privateKey, err := parsePrivateKeyFromPEMFile(keyPath)
	if err != nil {
		return tls.Certificate{}, fmt.Errorf("Unable to load PEM certificate '%s': %v", certificatePath, err)
	}

	// 叶证书为首个证书，其余按原顺序作为链（等价 C# ImportFromPemFile + 叶替换/插入）。
	leaf := certificates[0]
	chain := make([][]byte, 0, len(certificates))
	chain = append(chain, leaf.Raw)
	for _, cert := range certificates[1:] {
		chain = append(chain, cert.Raw)
	}

	return tls.Certificate{
		Certificate: chain,
		PrivateKey:  privateKey,
		Leaf:        leaf,
	}, nil
}

func parseCertificatesFromPEMFile(path string) ([]*x509.Certificate, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	var certificates []*x509.Certificate
	rest := raw
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		if block.Type != "CERTIFICATE" {
			continue
		}
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, err
		}
		certificates = append(certificates, cert)
	}
	return certificates, nil
}

func parsePrivateKeyFromPEMFile(path string) (any, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	rest := raw
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			return nil, errors.New("PEM file does not contain a private key.")
		}
		switch block.Type {
		case "PRIVATE KEY":
			key, err := x509.ParsePKCS8PrivateKey(block.Bytes)
			if err != nil {
				return nil, err
			}
			if validKey(key) {
				return key, nil
			}
		case "RSA PRIVATE KEY":
			key, err := x509.ParsePKCS1PrivateKey(block.Bytes)
			if err != nil {
				return nil, err
			}
			return key, nil
		case "EC PRIVATE KEY":
			key, err := x509.ParseECPrivateKey(block.Bytes)
			if err != nil {
				return nil, err
			}
			return key, nil
		}
	}
}

func validKey(key any) bool {
	switch key.(type) {
	case *rsa.PrivateKey, *ecdsa.PrivateKey, ed25519.PrivateKey:
		return true
	default:
		return false
	}
}

// loadPKCS12 对应 C# PFX 分支：无密码 PFX/P12（实现首日验证无加密 PFX）。
func loadPKCS12(path string) (tls.Certificate, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return tls.Certificate{}, err
	}

	privateKey, certificate, caCerts, err := pkcs12.DecodeChain(raw, "")
	if err != nil {
		return tls.Certificate{}, fmt.Errorf("TLS PFX must include an unencrypted private key: %v", err)
	}
	if certificate == nil || privateKey == nil {
		return tls.Certificate{}, errors.New("TLS PFX must include an unencrypted private key.")
	}

	chain := make([][]byte, 0, 1+len(caCerts))
	chain = append(chain, certificate.Raw)
	for _, cert := range caCerts {
		chain = append(chain, cert.Raw)
	}

	return tls.Certificate{
		Certificate: chain,
		PrivateKey:  privateKey,
		Leaf:        certificate,
	}, nil
}
