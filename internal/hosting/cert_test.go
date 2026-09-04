package hosting_test

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/hosting"
)

// generateSelfSignedRsaPem 生成自签 RSA 证书与 PKCS8 私钥 PEM。
func generateSelfSignedRsaPem(t *testing.T) (string, string) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	require.NoError(t, err)
	template := x509.Certificate{
		Subject:               pkix.Name{CommonName: "localhost"},
		NotBefore:             time.Now().Add(-5 * time.Minute),
		NotAfter:              time.Now().Add(24 * time.Hour),
		SerialNumber:          big.NewInt(1),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment | x509.KeyUsageCertSign,
		BasicConstraintsValid: true,
		IsCA:                  true,
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	require.NoError(t, err)
	certPem := string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
	keyPKCS8, err := x509.MarshalPKCS8PrivateKey(key)
	require.NoError(t, err)
	keyPem := string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyPKCS8}))
	return certPem, keyPem
}

func generateSelfSignedEcdsaPem(t *testing.T) (string, string) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	require.NoError(t, err)
	template := x509.Certificate{
		Subject:               pkix.Name{CommonName: "localhost"},
		NotBefore:             time.Now().Add(-5 * time.Minute),
		NotAfter:              time.Now().Add(24 * time.Hour),
		SerialNumber:          big.NewInt(1),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign,
		BasicConstraintsValid: true,
		IsCA:                  true,
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	require.NoError(t, err)
	certPem := string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
	keyDER, err := x509.MarshalECPrivateKey(key)
	require.NoError(t, err)
	keyPem := string(pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}))
	return certPem, keyPem
}

func TestLoadPemCertificateSupportsRsaCertAndSeparateKey(t *testing.T) {
	dir := t.TempDir()
	certPem, keyPem := generateSelfSignedRsaPem(t)
	certPath := filepath.Join(dir, "server.crt")
	keyPath := filepath.Join(dir, "server.key")
	require.NoError(t, os.WriteFile(certPath, []byte(certPem), 0o644))
	require.NoError(t, os.WriteFile(keyPath, []byte(keyPem), 0o644))

	loaded, err := hosting.LoadCertificate(certPath)
	require.NoError(t, err)
	assert.NotNil(t, loaded.PrivateKey)
	assert.NotEmpty(t, loaded.Certificate)
	assert.Equal(t, loaded.Certificate[0], loaded.Leaf.Raw)
}

func TestLoadPemCertificateSupportsCombinedPem(t *testing.T) {
	dir := t.TempDir()
	certPem, keyPem := generateSelfSignedRsaPem(t)
	pemPath := filepath.Join(dir, "server.pem")
	require.NoError(t, os.WriteFile(pemPath, []byte(certPem+"\n"+keyPem), 0o644))

	loaded, err := hosting.LoadCertificate(pemPath)
	require.NoError(t, err)
	assert.NotNil(t, loaded.PrivateKey)
	assert.NotEmpty(t, loaded.Certificate)
}

func TestLoadPemCertificateSupportsEcdsaCertAndSeparateKey(t *testing.T) {
	dir := t.TempDir()
	certPem, keyPem := generateSelfSignedEcdsaPem(t)
	certPath := filepath.Join(dir, "server.crt")
	keyPath := filepath.Join(dir, "server.key")
	require.NoError(t, os.WriteFile(certPath, []byte(certPem), 0o644))
	require.NoError(t, os.WriteFile(keyPath, []byte(keyPem), 0o644))

	loaded, err := hosting.LoadCertificate(certPath)
	require.NoError(t, err)
	assert.NotNil(t, loaded.PrivateKey)
	assert.NotEmpty(t, loaded.Certificate)
}

func TestLoadPemCertificateWrapsMissingPrivateKey(t *testing.T) {
	dir := t.TempDir()
	certPem, _ := generateSelfSignedRsaPem(t)
	certPath := filepath.Join(dir, "server.pem")
	require.NoError(t, os.WriteFile(certPath, []byte(certPem), 0o644))

	_, err := hosting.LoadCertificate(certPath)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "Unable to load PEM certificate")
}

func TestLoadPemCertificateRejectsNoCertificate(t *testing.T) {
	dir := t.TempDir()
	certPath := filepath.Join(dir, "empty.pem")
	require.NoError(t, os.WriteFile(certPath, []byte("random garbage content"), 0o644))

	_, err := hosting.LoadCertificate(certPath)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "Unable to load PEM certificate")
}

func TestLoadCertificateRejectsUnknownExtension(t *testing.T) {
	dir := t.TempDir()
	certPath := filepath.Join(dir, "server.der")
	require.NoError(t, os.WriteFile(certPath, []byte("x"), 0o644))
	_, err := hosting.LoadCertificate(certPath)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "must use .pem, .crt, .pfx, or .p12")
}

func TestLoadCertificateRejectsMissingFile(t *testing.T) {
	_, err := hosting.LoadCertificate(filepath.Join(t.TempDir(), "missing.pem"))
	require.Error(t, err)
	assert.True(t, strings.Contains(err.Error(), "was not found"))
}

func TestLoadCertificateRejectsEmptyPath(t *testing.T) {
	_, err := hosting.LoadCertificate("   ")
	require.Error(t, err)
	assert.Contains(t, err.Error(), "must not be empty")
}
