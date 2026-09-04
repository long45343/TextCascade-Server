package protocol

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
)

const validHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

func newTestConfig() *config.RuntimeConfig {
	cfg := config.Defaults()
	return &cfg
}

func parseJSON(t *testing.T, json string) (Message, *Error) {
	t.Helper()
	return ParseClientMessage([]byte(json), newTestConfig())
}

// ---- ProtocolParseTests ----

func TestParsesHello(t *testing.T) {
	result, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"n","lastServerVersion":5,"snapshot":{"payload":"p","encrypted":true,"hash":"`+validHash+`","localModifiedAtUtc":"2026-08-18T08:00:00Z"}}`)
	require.Nil(t, err)
	assert.Equal(t, KindHello, result.Kind)
	require.NotNil(t, result.Hello)
	assert.Equal(t, "a", result.Hello.ClientID)
	assert.EqualValues(t, 5, result.Hello.LastServerVersion)
	assert.NotNil(t, result.Hello.Snapshot)
}

func TestParsesClip(t *testing.T) {
	result, err := parseJSON(t, `{"type":"clip","id":"id1","payload":"p","encrypted":true,"hash":"`+validHash+`"}`)
	require.Nil(t, err)
	assert.Equal(t, KindClip, result.Kind)
	require.NotNil(t, result.Clip)
	assert.Equal(t, "id1", result.Clip.ID)
}

func TestParsesPong(t *testing.T) {
	result, err := parseJSON(t, `{"type":"pong","clientTimeUtc":"2026-08-18T08:02:00Z"}`)
	require.Nil(t, err)
	assert.Equal(t, KindPong, result.Kind)
}

func TestRejectsUnknownType(t *testing.T) {
	_, err := parseJSON(t, `{"type":"bogus"}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsMalformedJSON(t *testing.T) {
	_, err := parseJSON(t, "{not json")
	assert.NotNil(t, err)
}

func TestRejectsUnknownField(t *testing.T) {
	_, err := parseJSON(t, `{"type":"clip","id":"id1","payload":"p","encrypted":true,"hash":"h","extra":1}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsDuplicateField(t *testing.T) {
	_, err := parseJSON(t, `{"type":"clip","type":"clip","id":"id1","payload":"p","encrypted":true,"hash":"h"}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsEmptyClipPayload(t *testing.T) {
	_, err := parseJSON(t, `{"type":"clip","id":"id1","payload":"","encrypted":true,"hash":"h"}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrEmptyText, err.Code)
}

func TestRejectsOversizedPayload(t *testing.T) {
	cfg := newTestConfig()
	payload := strings.Repeat("x", cfg.Limits.MaxTextBytes+1)
	json := `{"type":"clip","id":"id1","payload":"` + payload + `","encrypted":true,"hash":"h"}`
	_, err := ParseClientMessage([]byte(json), cfg)
	require.NotNil(t, err)
	assert.Equal(t, ErrTextTooLarge, err.Code)
}

// ---- ProtocolSerializationTests ----

func TestWelcomeLatestTimeUsesUtcSecondFormat(t *testing.T) {
	timestamp := time.UnixMilli(1760000000123).UTC()
	latest := &LatestText{Payload: "payload", Version: 7, Hash: "hash", Encrypted: true, FromClientID: "client", FromClientName: "name", UpdatedAtUtc: timestamp}

	jsonText := string(MarshalWelcome(latest))
	assert.Contains(t, jsonText, `"updatedAtUtc":"2025-10-09T08:53:20Z"`)
}

func TestClipTimeUsesUtcSecondFormat(t *testing.T) {
	timestamp := time.UnixMilli(1760000000999).UTC()
	latest := &LatestText{Payload: "payload", Version: 7, Hash: "hash", Encrypted: true, FromClientID: "client", FromClientName: "name", UpdatedAtUtc: timestamp}

	jsonText := string(MarshalClip("clip-1", latest))
	assert.Contains(t, jsonText, `"updatedAtUtc":"2025-10-09T08:53:20Z"`)
}

func TestClipAckTimeUsesUtcSecondFormat(t *testing.T) {
	timestamp := time.UnixMilli(1760000000500).UTC()
	latest := &LatestText{Payload: "payload", Version: 7, Hash: "hash", Encrypted: true, FromClientID: "client", FromClientName: "name", UpdatedAtUtc: timestamp}

	jsonText := string(MarshalClipAck("clip-1", latest))
	assert.Contains(t, jsonText, `"updatedAtUtc":"2025-10-09T08:53:20Z"`)
}

// ---- ContractSchemaInvariants ----

func TestC1WelcomeNoLatestOmitsKey(t *testing.T) {
	jsonText := string(MarshalWelcome(nil))
	assert.Equal(t, `{"type":"welcome","protocolVersion":1}`, jsonText)
	assert.NotContains(t, jsonText, "latest")
}

func TestC2WelcomeWithLatestFixedFieldOrder(t *testing.T) {
	latest := &LatestText{
		Payload: "payload-text", Version: 128, Hash: "hash", Encrypted: true,
		FromClientID: "android-a", FromClientName: "android",
		UpdatedAtUtc: time.Date(2026, 8, 18, 7, 59, 58, 0, time.UTC),
	}
	jsonText := string(MarshalWelcome(latest))
	assert.Equal(t, `{"type":"welcome","protocolVersion":1,"latest":{"payload":"payload-text","version":128,"hash":"hash","encrypted":true,"fromClientId":"android-a","fromClientName":"android","updatedAtUtc":"2026-08-18T07:59:58Z"}}`, jsonText)
}

func TestC3BroadcastClipContainsAllEightFields(t *testing.T) {
	latest := &LatestText{
		Payload: "payload-text", Version: 129, Hash: "hash", Encrypted: false,
		FromClientID: "windows-a", FromClientName: "Windows Desktop",
		UpdatedAtUtc: time.Date(2026, 8, 18, 8, 1, 0, 0, time.UTC),
	}
	jsonText := string(MarshalClip("clip-id-1", latest))
	assert.Equal(t, `{"type":"clip","version":129,"id":"clip-id-1","payload":"payload-text","encrypted":false,"hash":"hash","fromClientId":"windows-a","fromClientName":"Windows Desktop","updatedAtUtc":"2026-08-18T08:01:00Z"}`, jsonText)
}

func TestC5ErrorResponseIncludesReferenceIDWhenNotNull(t *testing.T) {
	refID := "clip-1"
	withReference := string(MarshalError(&Error{Code: ErrTextTooLarge, Message: "Text exceeds maxTextBytes.", ReferenceID: &refID}))
	assert.Equal(t, `{"type":"error","code":"text_too_large","message":"Text exceeds maxTextBytes.","referenceId":"clip-1"}`, withReference)

	withoutReference := string(MarshalError(&Error{Code: ErrInvalidMessage, Message: "Invalid JSON."}))
	assert.Equal(t, `{"type":"error","code":"invalid_message","message":"Invalid JSON."}`, withoutReference)
}

func TestC6PingTimestampsUtcZSecondPrecision(t *testing.T) {
	now := time.Date(2026, 8, 18, 8, 2, 0, 456000000, time.UTC)
	jsonText := string(MarshalPing(now))
	assert.Equal(t, `{"type":"ping","serverTimeUtc":"2026-08-18T08:02:00Z"}`, jsonText)
}

func TestClipAckShapeMatchesContract(t *testing.T) {
	latest := &LatestText{
		Payload: "payload", Version: 129, Hash: "hash", Encrypted: false,
		FromClientID: "windows-a", FromClientName: "Windows Desktop",
		UpdatedAtUtc: time.Date(2026, 8, 18, 8, 1, 0, 0, time.UTC),
	}
	jsonText := string(MarshalClipAck("clip-id-1", latest))
	assert.Equal(t, `{"type":"clip_ack","id":"clip-id-1","version":129,"updatedAtUtc":"2026-08-18T08:01:00Z"}`, jsonText)
}

func TestByeShapeMatchesContract(t *testing.T) {
	jsonText := string(MarshalBye("server_shutdown"))
	assert.Equal(t, `{"type":"bye","reason":"server_shutdown"}`, jsonText)
}

// ---- ContractSampleTests（全矩阵，testdata/contract-samples）----

func contractSamplesRoot(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	require.NoError(t, err)
	for {
		candidate := filepath.Join(dir, "testdata", "contract-samples")
		if info, statErr := os.Stat(candidate); statErr == nil && info.IsDir() {
			return candidate
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatal("contract-samples directory not found")
		}
		dir = parent
	}
}

func collectSamples(t *testing.T, root, sub string) []string {
	t.Helper()
	var files []string
	_ = filepath.Walk(filepath.Join(root, sub), func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if !info.IsDir() {
			files = append(files, path)
		}
		return nil
	})
	sort.Strings(files)
	return files
}

func TestAllInvalidSamplesAreRejectedWithExpectedCode(t *testing.T) {
	root := contractSamplesRoot(t)
	samples := collectSamples(t, root, "invalid")
	require.NotEmpty(t, samples)
	cfg := newTestConfig()

	for _, path := range samples {
		path := path
		t.Run(filepath.ToSlash(strings.TrimPrefix(path, root+"/invalid/")), func(t *testing.T) {
			frame, err := os.ReadFile(path)
			require.NoError(t, err)

			_, parseErr := ParseClientMessage(frame, cfg)
			require.NotNil(t, parseErr, "sample should be rejected: %s", path)

			relative := strings.TrimPrefix(filepath.ToSlash(path), filepath.ToSlash(root)+"/invalid/")
			category := strings.SplitN(relative, "/", 2)[0]
			expected := "invalid_message"
			if category == "frame_too_large" {
				expected = "frame_too_large"
			}
			assert.Equal(t, expected, parseErr.CodeName())
		})
	}
}

func TestAllValidSamplesParseWithExpectedKind(t *testing.T) {
	root := contractSamplesRoot(t)
	samples := collectSamples(t, root, "valid")
	require.NotEmpty(t, samples)
	cfg := newTestConfig()

	for _, path := range samples {
		path := path
		base := filepath.Base(path)
		var expected Kind
		switch {
		case strings.HasPrefix(base, "hello."):
			expected = KindHello
		case strings.HasPrefix(base, "clip."):
			expected = KindClip
		case strings.HasPrefix(base, "pong."):
			expected = KindPong
		default:
			t.Fatalf("Cannot infer kind from %s", path)
		}

		t.Run(base, func(t *testing.T) {
			frame, err := os.ReadFile(path)
			require.NoError(t, err)

			result, parseErr := ParseClientMessage(frame, cfg)
			require.Nil(t, parseErr, "Sample should parse: %s error=%v", path, parseErr)
			assert.Equal(t, expected, result.Kind)
		})
	}
}

func TestValidHelloFullParsesAllFields(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "hello.full.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Hello)
	hello := result.Hello
	assert.Equal(t, "windows-a", hello.ClientID)
	assert.Equal(t, "Windows Desktop", hello.ClientName)
	assert.EqualValues(t, 128, hello.LastServerVersion)

	require.NotNil(t, hello.Snapshot)
	assert.Equal(t, "clipboard text", hello.Snapshot.Payload)
	assert.True(t, hello.Snapshot.Encrypted)
	assert.Equal(t, "sha256-hex", hello.Snapshot.Hash)
	assert.Equal(t, time.Date(2026, 8, 18, 8, 0, 0, 0, time.UTC), hello.Snapshot.LocalModifiedAtUtc)
}

func TestValidHelloMinimalHasNoSnapshot(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "hello.minimal.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Hello)
	assert.Nil(t, result.Hello.Snapshot)
	assert.EqualValues(t, 0, result.Hello.LastServerVersion)
	assert.Equal(t, "", result.Hello.ClientName)
}

func TestValidHelloNullSnapshotIsExplicitNull(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "hello.null-snapshot.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Hello)
	assert.Nil(t, result.Hello.Snapshot)
}

func TestValidClipParsesAllFields(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "clip.basic.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Clip)
	clip := result.Clip
	assert.Equal(t, "clip-20260818-001", clip.ID)
	assert.Equal(t, "shared clipboard content", clip.Payload)
	assert.False(t, clip.Encrypted)
	assert.Equal(t, "sha256-hex", clip.Hash)
}

func TestValidPongParsesTimestamp(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "pong.ok.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Pong)
	assert.Equal(t, time.Date(2026, 8, 18, 8, 2, 0, 0, time.UTC), result.Pong.ClientTimeUtc)
}

func TestValidHelloRoundTripTimestampFormatIsAccepted(t *testing.T) {
	root := contractSamplesRoot(t)
	frame, err := os.ReadFile(filepath.Join(root, "valid", "hello.snapshot-roundtrip-timestamp.json"))
	require.NoError(t, err)

	result, parseErr := ParseClientMessage(frame, newTestConfig())
	require.Nil(t, parseErr)
	require.NotNil(t, result.Hello)
	require.NotNil(t, result.Hello.Snapshot)
	assert.Equal(t, 0*time.Second, time.Duration(zeroOffset(result.Hello.Snapshot.LocalModifiedAtUtc)))
}

// ---- 解析边界补充（等价 C# 深度/转义行为）----

func TestRejectsDepthExceeded(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":0,"snapshot":{"payload":"text","encrypted":true,"hash":"h","localModifiedAtUtc":"2026-08-18T08:00:00Z"},"l1":{"l2":{"l3":{"l4":1}}}}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsFractionLastServerVersion(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":1.5,"snapshot":null}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsExponentLastServerVersion(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":1e3,"snapshot":null}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsNegativeLastServerVersion(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":-1,"snapshot":null}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsStringLastServerVersion(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":"1","snapshot":null}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsTooLargeLastServerVersion(t *testing.T) {
	_, err := parseJSON(t, `{"type":"hello","clientId":"a","clientName":"","lastServerVersion":18446744073709551616,"snapshot":null}`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsRootNotObject(t *testing.T) {
	_, err := parseJSON(t, `[1,2,3]`)
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestRejectsInvalidUTF8(t *testing.T) {
	frame := []byte{'{', '"', 't', 'y', 'p', 'e', '"', ':', '"', 'c', 'l', 'i', 'p', '"', ',', 0xFF, '}'}
	_, err := ParseClientMessage(frame, newTestConfig())
	require.NotNil(t, err)
	assert.Equal(t, ErrInvalidMessage, err.Code)
}

func TestParseFlexibleTimeForms(t *testing.T) {
	build := func(value string) *Node {
		root, err := Decode([]byte(`{"t":"`+value+`"}`), 2)
		require.NoError(t, err)
		return root
	}

	// 秒级
	value, ok := ParseFlexibleTime(build("2026-08-18T08:00:00Z"), "t")
	assert.True(t, ok)
	assert.Equal(t, time.Date(2026, 8, 18, 8, 0, 0, 0, time.UTC), value)

	// 7 位往返
	value, ok = ParseFlexibleTime(build("2026-08-18T08:00:00.1234567Z"), "t")
	assert.True(t, ok)
	assert.Equal(t, time.Date(2026, 8, 18, 8, 0, 0, 123456700, time.UTC), value)

	// 非 Z 偏移拒绝
	_, ok = ParseFlexibleTime(build("2026-08-18T08:00:00+00:00"), "t")
	assert.False(t, ok)

	// 数字拒绝
	_, ok = ParseFlexibleTime(build("1760000000"), "t")
	assert.False(t, ok)
}

func zeroOffset(t time.Time) int {
	_, offset := t.Zone()
	return offset
}
