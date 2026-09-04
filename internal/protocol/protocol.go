// Package protocol：Protocol.cs 的结构、入站解析与校验半边（本文件），
// 出站 marshal 见 serialize.go。
package protocol

import (
	"strconv"
	"strings"
	"time"

	"github.com/long45343/TextCascade-Server/internal/config"
)

const (
	ProtocolVersion = 1

	MaxNameBytes = 128

	MaxIdBytes = 128

	MaxHashBytes = 4096
)

// ErrKind 对应 C# ProtocolErrorCode。
type ErrKind int

const (
	ErrInvalidMessage ErrKind = iota
	ErrTextTooLarge
	ErrFrameTooLarge
	ErrEmptyText
	ErrRateLimited
	ErrHelloTimeout
	ErrServerBusy
)

// Error 对应 C# ProtocolError record。
type Error struct {
	Code        ErrKind
	Message     string
	ReferenceID *string
}

func (e *Error) CodeName() string {
	switch e.Code {
	case ErrTextTooLarge:
		return "text_too_large"
	case ErrFrameTooLarge:
		return "frame_too_large"
	case ErrEmptyText:
		return "empty_text"
	case ErrRateLimited:
		return "rate_limited"
	case ErrHelloTimeout:
		return "hello_timeout"
	case ErrServerBusy:
		return "server_busy"
	default:
		return "invalid_message"
	}
}

func invalid(message string, referenceID *string) *Error {
	return &Error{Code: ErrInvalidMessage, Message: message, ReferenceID: referenceID}
}

// ClipSnapshot 对应 C# ClipSnapshot record。
type ClipSnapshot struct {
	Payload            string
	Encrypted          bool
	Hash               string
	LocalModifiedAtUtc time.Time
}

// ClientHello 对应 C# ClientHello record。
type ClientHello struct {
	ClientID          string
	ClientName        string
	LastServerVersion uint64
	Snapshot          *ClipSnapshot
}

// ClientClip 对应 C# ClientClip record。
type ClientClip struct {
	ID        string
	Payload   string
	Encrypted bool
	Hash      string
}

// ClientPong 对应 C# ClientPong record。
type ClientPong struct {
	ClientTimeUtc time.Time
}

// Kind 对应 C# MessageKind。
type Kind int

const (
	KindUnknown Kind = iota
	KindHello
	KindClip
	KindPong
)

// Message 是单一 struct（Go 无判别联合，用固定字段）。
type Message struct {
	Kind  Kind
	Hello *ClientHello
	Clip  *ClientClip
	Pong  *ClientPong
}

// LatestText 对应 C# LatestText record。
type LatestText struct {
	Payload        string
	Version        uint64
	Hash           string
	Encrypted      bool
	FromClientID   string
	FromClientName string
	UpdatedAtUtc   time.Time
}

// LatestFromSnapshot 对应 C# LatestText.From。
func LatestFromSnapshot(s *ClipSnapshot, version uint64, clientID, clientName string) *LatestText {
	return &LatestText{
		Payload:        s.Payload,
		Version:        version,
		Hash:           s.Hash,
		Encrypted:      s.Encrypted,
		FromClientID:   clientID,
		FromClientName: clientName,
		UpdatedAtUtc:   s.LocalModifiedAtUtc,
	}
}

// ParseClientMessage 顺序：jsonscan 预扫描 → 根必须 object → type 字符串 →
// 未知/重复字段检查（known 集合按类型）→ parseHello/parseClip/parsePong → 未知 type。
func ParseClientMessage(frame []byte, cfg *config.RuntimeConfig) (Message, *Error) {
	root, err := Decode(frame, 3)
	if err != nil {
		return Message{}, invalid("Invalid JSON: "+err.Error(), nil)
	}

	if !root.IsObject() {
		return Message{}, invalid("Root must be an object.", nil)
	}

	typeValue := root.Get("type")
	if typeValue == nil || !typeValue.IsString() {
		return Message{}, invalid("Missing or invalid type.", getReferenceID(root))
	}
	messageType := typeValue.Str()

	var known []string
	switch messageType {
	case "hello":
		known = []string{"type", "clientId", "clientName", "lastServerVersion", "snapshot"}
	case "clip":
		known = []string{"type", "id", "payload", "encrypted", "hash"}
	case "pong":
		known = []string{"type", "clientTimeUtc"}
	default:
		known = nil
	}

	if propertyName, ok := hasUniqueKnownProperties(root, known); !ok {
		return Message{}, invalid("Unknown or duplicate field: "+propertyName+".", getReferenceID(root))
	}

	switch messageType {
	case "hello":
		return parseHello(root, cfg)
	case "clip":
		return parseClip(root, cfg)
	case "pong":
		return parsePong(root)
	default:
		return Message{}, invalid("Unknown message type.", getReferenceID(root))
	}
}

func parseHello(root *Node, cfg *config.RuntimeConfig) (Message, *Error) {
	clientIDValue := root.Get("clientId")
	if clientIDValue == nil || !clientIDValue.IsString() || utf8Len(clientIDValue.Str()) > MaxNameBytes || clientIDValue.Str() == "" {
		return Message{}, invalid("clientId must be 1-128 bytes.", nil)
	}
	clientID := clientIDValue.Str()

	var clientName string
	if nameValue := root.Get("clientName"); nameValue != nil && nameValue.IsString() && utf8Len(nameValue.Str()) <= MaxNameBytes {
		clientName = nameValue.Str()
	}

	lastVersion, ok := tryGetUint64(root, "lastServerVersion")
	if !ok {
		return Message{}, invalid("lastServerVersion must be a non-negative integer.", nil)
	}

	if snapshotElement := root.Get("snapshot"); snapshotElement != nil {
		if !snapshotElement.IsObject() && !snapshotElement.IsNull() {
			return Message{}, invalid("snapshot must be an object or null.", nil)
		}

		if snapshotElement.IsObject() {
			if propertyName, ok := hasUniqueKnownProperties(snapshotElement, []string{"payload", "encrypted", "hash", "localModifiedAtUtc"}); !ok {
				return Message{}, invalid("Unknown or duplicate field: "+propertyName+".", nil)
			}

			snapshot, snapErr := tryGetSnapshot(snapshotElement, cfg)
			if snapErr != nil {
				return Message{}, snapErr
			}

			hello := &ClientHello{ClientID: clientID, ClientName: clientName, LastServerVersion: lastVersion, Snapshot: snapshot}
			if ValidateHello(hello, cfg) {
				return Message{Kind: KindHello, Hello: hello}, nil
			}
			return Message{}, invalid("hello validation failed.", nil)
		}
	}

	plainHello := &ClientHello{ClientID: clientID, ClientName: clientName, LastServerVersion: lastVersion, Snapshot: nil}
	if ValidateHello(plainHello, cfg) {
		return Message{Kind: KindHello, Hello: plainHello}, nil
	}
	return Message{}, invalid("hello validation failed.", nil)
}

func tryGetSnapshot(element *Node, cfg *config.RuntimeConfig) (*ClipSnapshot, *Error) {
	payloadValue := element.Get("payload")
	if payloadValue == nil || !payloadValue.IsString() || payloadValue.Str() == "" {
		return nil, invalid("snapshot.payload is required and must not be empty.", nil)
	}
	payload := payloadValue.Str()

	if utf8Len(payload) > cfg.Limits.MaxTextBytes {
		return nil, &Error{Code: ErrTextTooLarge, Message: "Text exceeds maxTextBytes."}
	}

	encryptedValue := element.Get("encrypted")
	if encryptedValue == nil || !encryptedValue.IsBool() {
		return nil, invalid("snapshot.encrypted must be a boolean.", nil)
	}
	encrypted := encryptedValue.Bool()

	hashValue := element.Get("hash")
	if hashValue == nil || !hashValue.IsString() || utf8Len(hashValue.Str()) > MaxHashBytes || hashValue.Str() == "" {
		return nil, invalid("snapshot.hash is required.", nil)
	}
	hash := hashValue.Str()

	modified, ok := ParseFlexibleTime(element, "localModifiedAtUtc")
	if !ok {
		return nil, invalid("snapshot.localModifiedAtUtc must be UTC RFC3339.", nil)
	}

	return &ClipSnapshot{Payload: payload, Encrypted: encrypted, Hash: hash, LocalModifiedAtUtc: modified}, nil
}

func parseClip(root *Node, cfg *config.RuntimeConfig) (Message, *Error) {
	idValue := root.Get("id")
	if idValue == nil || !idValue.IsString() || utf8Len(idValue.Str()) > MaxIdBytes || idValue.Str() == "" {
		return Message{}, invalid("id must be 1-128 bytes.", nil)
	}
	id := idValue.Str()

	payloadValue := root.Get("payload")
	if payloadValue == nil || !payloadValue.IsString() || payloadValue.Str() == "" {
		return Message{}, &Error{Code: ErrEmptyText, Message: "payload must not be empty.", ReferenceID: &id}
	}
	payload := payloadValue.Str()

	if utf8Len(payload) > cfg.Limits.MaxTextBytes {
		return Message{}, &Error{Code: ErrTextTooLarge, Message: "Text exceeds maxTextBytes.", ReferenceID: &id}
	}

	encryptedValue := root.Get("encrypted")
	if encryptedValue == nil || !encryptedValue.IsBool() {
		return Message{}, invalid("encrypted must be a boolean.", &id)
	}
	encrypted := encryptedValue.Bool()

	hashValue := root.Get("hash")
	if hashValue == nil || !hashValue.IsString() || utf8Len(hashValue.Str()) > MaxHashBytes || hashValue.Str() == "" {
		return Message{}, invalid("hash is required.", &id)
	}
	hash := hashValue.Str()

	clip := &ClientClip{ID: id, Payload: payload, Encrypted: encrypted, Hash: hash}
	if ValidateClip(clip, cfg) {
		return Message{Kind: KindClip, Clip: clip}, nil
	}
	return Message{}, invalid("clip validation failed.", &id)
}

func parsePong(root *Node) (Message, *Error) {
	clientTime, ok := ParseFlexibleTime(root, "clientTimeUtc")
	if !ok {
		return Message{}, invalid("clientTimeUtc must be UTC RFC3339.", nil)
	}

	return Message{Kind: KindPong, Pong: &ClientPong{ClientTimeUtc: clientTime}}, nil
}

// ValidateHello 对应 C# ValidateHello。
func ValidateHello(hello *ClientHello, cfg *config.RuntimeConfig) bool {
	return utf8Len(hello.ClientID) > 0 && utf8Len(hello.ClientID) <= MaxNameBytes &&
		utf8Len(hello.ClientName) <= MaxNameBytes &&
		(hello.Snapshot == nil || validateSnapshot(hello.Snapshot, cfg))
}

func validateSnapshot(snapshot *ClipSnapshot, cfg *config.RuntimeConfig) bool {
	return len(snapshot.Payload) > 0 &&
		utf8Len(snapshot.Payload) <= cfg.Limits.MaxTextBytes &&
		utf8Len(snapshot.Hash) <= MaxHashBytes &&
		isUTCZeroOffset(snapshot.LocalModifiedAtUtc)
}

// ValidateClip 对应 C# ValidateClipMessage。
func ValidateClip(message *ClientClip, cfg *config.RuntimeConfig) bool {
	return len(message.ID) > 0 &&
		utf8Len(message.ID) <= MaxIdBytes &&
		len(message.Payload) > 0 &&
		utf8Len(message.Payload) <= cfg.Limits.MaxTextBytes &&
		len(message.Hash) > 0 &&
		utf8Len(message.Hash) <= MaxHashBytes
}

// CheckFrameSize 对应 C# CheckFrameSize。
func CheckFrameSize(frameLength int, cfg *config.RuntimeConfig) bool {
	return frameLength > 0 && frameLength <= cfg.Limits.MaxFrameBytes
}

// CheckPayloadSize 对应 C# CheckPayloadSize。
func CheckPayloadSize(payload string, cfg *config.RuntimeConfig) bool {
	return utf8Len(payload) <= cfg.Limits.MaxTextBytes
}

func getReferenceID(root *Node) *string {
	if root.IsObject() {
		if id := root.Get("id"); id != nil && id.IsString() {
			value := id.Str()
			return &value
		}
	}
	return nil
}

// hasUniqueKnownProperties 复刻 C# HasUniqueKnownProperties：按文档序，
// 第一个"不在 known 集合中或重复"的字段名即失败原因。
func hasUniqueKnownProperties(root *Node, known []string) (string, bool) {
	knownSet := make(map[string]struct{}, len(known))
	for _, key := range known {
		knownSet[key] = struct{}{}
	}
	seen := make(map[string]struct{}, len(known))
	for _, member := range root.Members() {
		_, knownMatch := knownSet[member.Key]
		_, dup := seen[member.Key]
		if !knownMatch || dup {
			return member.Key, false
		}
		seen[member.Key] = struct{}{}
	}
	return "", true
}

// tryGetUint64 复刻 C# TryGetUInt64：数字形态检查（无 -/./e/E）+ ParseUint。
func tryGetUint64(root *Node, name string) (uint64, bool) {
	element := root.Get(name)
	if element == nil || !element.IsNumber() {
		return 0, false
	}

	raw := element.RawNumber()
	if strings.HasPrefix(raw, "-") || strings.ContainsAny(raw, ".eE") {
		return 0, false
	}

	value, err := strconv.ParseUint(raw, 10, 64)
	if err != nil {
		return 0, false
	}
	return value, true
}

// ParseFlexibleTime 复刻 C# TryGetUtcDateTime：必须以 'Z' 结尾；
// 接受秒级（yyyy-MM-ddTHH:mm:ssZ）或 ISO 往返（含 1-7 位小数秒）两种形态；
// 偏移必须为零（Z 即零偏移）。
func ParseFlexibleTime(root *Node, name string) (time.Time, bool) {
	element := root.Get(name)
	if element == nil || !element.IsString() {
		return time.Time{}, false
	}

	text := element.Str()
	if !strings.HasSuffix(text, "Z") {
		return time.Time{}, false
	}

	value, ok := parseFlexibleUTC(text)
	if !ok {
		return time.Time{}, false
	}
	return value, true
}

func parseFlexibleUTC(text string) (time.Time, bool) {
	body := strings.TrimSuffix(text, "Z")
	const dateLayout = "2006-01-02"
	const secondsLayout = "15:04:05"
	tIndex := strings.IndexByte(body, 'T')
	if tIndex < 0 {
		return time.Time{}, false
	}
	datePart, timePart := body[:tIndex], body[tIndex+1:]

	day, err := time.Parse(dateLayout, datePart)
	if err != nil {
		return time.Time{}, false
	}

	fractionDigits := 0
	if dot := strings.IndexByte(timePart, '.'); dot >= 0 {
		fractionDigits = len(timePart) - dot - 1
		if fractionDigits < 1 || fractionDigits > 7 {
			return time.Time{}, false
		}
	}

	clock, err := time.Parse(secondsLayout, timePart)
	if err != nil {
		return time.Time{}, false
	}

	result := time.Date(
		day.Year(), day.Month(), day.Day(),
		clock.Hour(), clock.Minute(), clock.Second(),
		fractionNanoseconds(timePart, fractionDigits),
		time.UTC)
	return result, true
}

func fractionNanoseconds(timePart string, digits int) int {
	if digits == 0 {
		return 0
	}
	dot := strings.IndexByte(timePart, '.')
	frac := timePart[dot+1 : dot+1+digits]
	nanos, _ := strconv.Atoi(frac)
	for i := digits; i < 9; i++ {
		nanos *= 10
	}
	return nanos
}

func isUTCZeroOffset(t time.Time) bool {
	_, offset := t.Zone()
	return offset == 0
}

func utf8Len(s string) int {
	return len(s)
}
