// Package logging 是 SecurityLogging.cs 的 1:1 迁移，含 Q12 的
// 自定义单行 slog Handler（复刻 yyyy-MM-ddTHH:mm:ssZ 时间戳与扁平字段）。
package logging

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
)

// Handler 输出单行日志：
//
//	2026-09-05T08:00:00Z info security_event login username=alice ip=1.2.3.4 success=true
type Handler struct {
	mu    *sync.Mutex
	w     io.Writer
	attrs []slog.Attr
}

func NewHandler() *Handler {
	return &Handler{mu: &sync.Mutex{}, w: os.Stderr}
}

func NewHandlerWithWriter(w io.Writer) *Handler {
	return &Handler{mu: &sync.Mutex{}, w: w}
}

func (h *Handler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= slog.LevelInfo
}

func (h *Handler) Handle(_ context.Context, r slog.Record) error {
	var b strings.Builder
	b.WriteString(r.Time.UTC().Format("2006-01-02T15:04:05"))
	b.WriteString("Z ")
	b.WriteString(levelName(r.Level))
	b.WriteByte(' ')
	b.WriteString(r.Message)

	attrs := make([]slog.Attr, 0, len(h.attrs)+r.NumAttrs())
	attrs = append(attrs, h.attrs...)
	r.Attrs(func(a slog.Attr) bool {
		attrs = append(attrs, a)
		return true
	})

	names := make([]string, 0, len(attrs))
	byName := make(map[string]slog.Attr, len(attrs))
	for _, a := range attrs {
		if _, seen := byName[a.Key]; !seen {
			names = append(names, a.Key)
		}
		byName[a.Key] = a
	}
	sort.Strings(names)
	for _, name := range names {
		b.WriteByte(' ')
		b.WriteString(name)
		b.WriteByte('=')
		b.WriteString(formatValue(byName[name].Value))
	}
	b.WriteByte('\n')

	h.mu.Lock()
	defer h.mu.Unlock()
	_, err := io.WriteString(h.w, b.String())
	return err
}

func (h *Handler) WithAttrs(attrs []slog.Attr) slog.Handler {
	cloned := &Handler{mu: h.mu, w: h.w}
	cloned.attrs = append(cloned.attrs, h.attrs...)
	cloned.attrs = append(cloned.attrs, attrs...)
	return cloned
}

func (h *Handler) WithGroup(name string) slog.Handler {
	return h
}

func levelName(level slog.Level) string {
	switch {
	case level >= slog.LevelError:
		return "error"
	case level >= slog.LevelWarn:
		return "warn"
	case level >= slog.LevelInfo:
		return "info"
	default:
		return "debug"
	}
}

func formatValue(v slog.Value) string {
	switch v.Kind() {
	case slog.KindString:
		return v.String()
	case slog.KindBool:
		return strconv.FormatBool(v.Bool())
	case slog.KindInt64:
		return strconv.FormatInt(v.Int64(), 10)
	case slog.KindUint64:
		return strconv.FormatUint(v.Uint64(), 10)
	case slog.KindFloat64:
		return strconv.FormatFloat(v.Float64(), 'g', -1, 64)
	case slog.KindTime:
		return v.Time().UTC().Format("2006-01-02T15:04:05Z")
	case slog.KindDuration:
		return v.Duration().String()
	default:
		return fmt.Sprint(v.Any())
	}
}

// Field 是安全事件的一个键值对。
type Field struct {
	Key   string
	Value any
}

// SecurityEvent 是 C# LogSecurityEvent 扩展方法的等价物：
// 去重（同键后者覆盖、保留首现位置）、脱敏后以 info 级输出。
func SecurityEvent(logger *slog.Logger, eventName string, fields ...Field) {
	pairs := RedactFields(fields)
	args := make([]any, 0, len(pairs)*2)
	for _, pair := range pairs {
		args = append(args, pair.Key, pair.Value)
	}
	logger.Info("security_event "+eventName, args...)
}

// RedactFields 按键脱敏；规则与 C# SecurityLogger.RedactFields 一致。
func RedactFields(fields []Field) []Field {
	pairs := make([]Field, 0, len(fields))
	index := make(map[string]int, len(fields))
	for _, field := range fields {
		var value any
		switch field.Key {
		case "password", "token", "secret", "authorization", "passwordHash":
			value = "<redacted>"
		case "payload", "hash":
			if text, ok := field.Value.(string); ok {
				value = fmt.Sprintf("<%d chars>", len(text))
			} else {
				value = "<redacted>"
			}
		default:
			if text, ok := field.Value.(string); ok {
				value = RedactSensitive(field.Key, text)
			} else if field.Value == nil {
				value = "<null>"
			} else {
				value = fmt.Sprint(field.Value)
			}
		}

		if at, seen := index[field.Key]; seen {
			pairs[at] = Field{Key: field.Key, Value: value}
		} else {
			index[field.Key] = len(pairs)
			pairs = append(pairs, Field{Key: field.Key, Value: value})
		}
	}
	return pairs
}

// RedactSensitive 与 C# SecurityLogger.RedactSensitive 一致。
func RedactSensitive(key, value string) string {
	if value == "" {
		return value
	}

	switch key {
	case "password", "token", "secret", "authorization", "passwordHash", "payload", "hash":
		return "<redacted>"
	}

	return value
}

// TokenPrefix 与 C# SecurityLogger.TokenPrefix 一致（保留：生产不调用，1:1）。
func TokenPrefix(token string) string {
	if token == "" {
		return ""
	}

	if len(token) <= 8 {
		return strings.Repeat("*", len(token))
	}
	return token[:8]
}
