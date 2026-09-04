// Package protocol：出站 marshal 半边（serialize.go）。
// 全部手写字节、字段序固定（§7.7 出站表）；字符串转义复刻
// System.Text.Json 默认编码器（控制字符、HTML 敏感字符、非 ASCII 转 \uXXXX）。
package protocol

import (
	"strconv"
	"strings"
	"time"
	"unicode/utf16"
	"unicode/utf8"

	"github.com/long45343/TextCascade-Server/internal/config"
)

func utcSeconds(t time.Time) string {
	return t.UTC().Format("2006-01-02T15:04:05Z")
}

// MarshalWelcome 对应 C# SerializeWelcome：latest 非 nil 时续 ",\"latest\":{...}"。
func MarshalWelcome(latest *LatestText) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"welcome","protocolVersion":`)
	b.WriteString(strconv.Itoa(ProtocolVersion))
	if latest != nil {
		b.WriteString(`,"latest":`)
		b.Write(marshalLatest(latest))
	}
	b.WriteByte('}')
	return []byte(b.String())
}

// MarshalClip 对应 C# SerializeClip。
func MarshalClip(id string, latest *LatestText) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"clip","version":`)
	b.WriteString(strconv.FormatUint(latest.Version, 10))
	b.WriteString(`,"id":`)
	writeJSONString(&b, id)
	b.WriteString(`,"payload":`)
	writeJSONString(&b, latest.Payload)
	b.WriteString(`,"encrypted":`)
	b.WriteString(strconv.FormatBool(latest.Encrypted))
	b.WriteString(`,"hash":`)
	writeJSONString(&b, latest.Hash)
	b.WriteString(`,"fromClientId":`)
	writeJSONString(&b, latest.FromClientID)
	b.WriteString(`,"fromClientName":`)
	writeJSONString(&b, latest.FromClientName)
	b.WriteString(`,"updatedAtUtc":"`)
	b.WriteString(utcSeconds(latest.UpdatedAtUtc))
	b.WriteString(`"}`)
	return []byte(b.String())
}

// MarshalClipAck 对应 C# SerializeClipAck。
func MarshalClipAck(id string, latest *LatestText) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"clip_ack","id":`)
	writeJSONString(&b, id)
	b.WriteString(`,"version":`)
	b.WriteString(strconv.FormatUint(latest.Version, 10))
	b.WriteString(`,"updatedAtUtc":"`)
	b.WriteString(utcSeconds(latest.UpdatedAtUtc))
	b.WriteString(`"}`)
	return []byte(b.String())
}

// MarshalPing 对应 C# SerializePing。
func MarshalPing(now time.Time) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"ping","serverTimeUtc":"`)
	b.WriteString(utcSeconds(now))
	b.WriteString(`"}`)
	return []byte(b.String())
}

// MarshalBye 对应 C# SerializeBye。
func MarshalBye(reason string) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"bye","reason":`)
	writeJSONString(&b, reason)
	b.WriteByte('}')
	return []byte(b.String())
}

// MarshalError 对应 C# SerializeProtocolError；referenceId 为 nil 时键省略。
func MarshalError(err *Error) []byte {
	var b strings.Builder
	b.WriteString(`{"type":"error","code":"`)
	b.WriteString(err.CodeName())
	b.WriteString(`","message":`)
	writeJSONString(&b, err.Message)
	if err.ReferenceID != nil {
		b.WriteString(`,"referenceId":`)
		writeJSONString(&b, *err.ReferenceID)
	}
	b.WriteByte('}')
	return []byte(b.String())
}

// MarshalLoginResponse 对应 C# SerializeLoginResponse；needsRehash 仅 true 时输出。
// expiresAtUtc 使用 C# "O" 往返格式（7 位小数秒，UTC 渲染为 +00:00）。
func MarshalLoginResponse(compactToken string, expiresAtUnix int64, cfg *config.RuntimeConfig, needsRehash bool) []byte {
	var b strings.Builder
	b.WriteString(`{"token":`)
	writeJSONString(&b, compactToken)
	b.WriteString(`,"expiresAtUtc":"`)
	b.WriteString(expiresAtUtcFormat(expiresAtUnix))
	b.WriteString(`","protocolVersion":`)
	b.WriteString(strconv.Itoa(ProtocolVersion))
	b.WriteString(`,"maxTextBytes":`)
	b.WriteString(strconv.Itoa(cfg.Limits.MaxTextBytes))
	b.WriteString(`,"helloTimeoutSeconds":`)
	b.WriteString(strconv.Itoa(cfg.Limits.HelloTimeoutSeconds))
	b.WriteString(`,"heartbeatIntervalSeconds":`)
	b.WriteString(strconv.Itoa(cfg.Limits.HeartbeatIntervalSeconds))
	b.WriteString(`,"heartbeatTimeoutSeconds":`)
	b.WriteString(strconv.Itoa(cfg.Limits.HeartbeatTimeoutSeconds))
	if needsRehash {
		b.WriteString(`,"needsRehash":true`)
	}
	b.WriteByte('}')
	return []byte(b.String())
}

func marshalLatest(latest *LatestText) []byte {
	var b strings.Builder
	b.WriteString(`{"payload":`)
	writeJSONString(&b, latest.Payload)
	b.WriteString(`,"version":`)
	b.WriteString(strconv.FormatUint(latest.Version, 10))
	b.WriteString(`,"hash":`)
	writeJSONString(&b, latest.Hash)
	b.WriteString(`,"encrypted":`)
	b.WriteString(strconv.FormatBool(latest.Encrypted))
	b.WriteString(`,"fromClientId":`)
	writeJSONString(&b, latest.FromClientID)
	b.WriteString(`,"fromClientName":`)
	writeJSONString(&b, latest.FromClientName)
	b.WriteString(`,"updatedAtUtc":"`)
	b.WriteString(utcSeconds(latest.UpdatedAtUtc))
	b.WriteString(`"}`)
	return []byte(b.String())
}

// expiresAtUtcFormat 复刻 DateTimeOffset.ToString("O") 的 UTC 输出：
// 2026-09-05T12:00:00.0000000+00:00。布局用 "-07:00" 强制数值偏移（"Z07:00" 会渲染成 "Z"）。
func expiresAtUtcFormat(unixSeconds int64) string {
	return time.Unix(unixSeconds, 0).UTC().Format("2006-01-02T15:04:05.0000000-07:00")
}

const hexDigits = "0123456789abcdef"

// writeJSONString 按 System.Text.Json 默认编码器转义：
//   - 命名转义：\" \\ \b \f \n \r \t；
//   - 其余 <0x20 控制字符与 0x7F：\u00xx（小写十六进制）；
//   - HTML 敏感字符：& ' < > ` 与 +：\u00xx；
//   - U+0085、U+2028、U+2029 及全部非 ASCII：\uxxxx（UTF-16 码元，小写）。
func writeJSONString(b *strings.Builder, s string) {
	b.WriteByte('"')
	for _, r := range s {
		if r < utf8.RuneSelf {
			writeASCIIUnit(b, uint16(r))
			continue
		}
		if r == 0x2028 || r == 0x2029 {
			writeHexUnit(b, uint16(r))
			continue
		}
		hi, lo := utf16.EncodeRune(r)
		if lo == -1 { // 未成代理对的 BMP 字符
			writeHexUnit(b, uint16(hi))
			continue
		}
		writeHexUnit(b, uint16(hi))
		writeHexUnit(b, uint16(lo))
	}
	b.WriteByte('"')
}

func writeASCIIUnit(b *strings.Builder, c uint16) {
	switch c {
	case '"':
		b.WriteString(`\"`)
	case '\\':
		b.WriteString(`\\`)
	case '\b':
		b.WriteString(`\b`)
	case '\f':
		b.WriteString(`\f`)
	case '\n':
		b.WriteString(`\n`)
	case '\r':
		b.WriteString(`\r`)
	case '\t':
		b.WriteString(`\t`)
	case '&', '\'', '+', '<', '>', '`', 0x7F:
		writeHexUnit(b, c)
	default:
		if c < 0x20 {
			writeHexUnit(b, c)
			return
		}
		b.WriteByte(byte(c))
	}
}

func writeHexUnit(b *strings.Builder, unit uint16) {
	b.WriteString(`\u`)
	b.WriteByte(hexDigits[unit>>12&0xF])
	b.WriteByte(hexDigits[unit>>8&0xF])
	b.WriteByte(hexDigits[unit>>4&0xF])
	b.WriteByte(hexDigits[unit&0xF])
}
