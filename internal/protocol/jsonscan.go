// Package protocol 是 Protocol.cs 的 1:1 迁移。
// 本文件是 Q5 决策的 token 级预扫描器（§7.7-A）：
//  1. UTF-8 完整性检查（非法即拒绝）；
//  2. 单一顶层 JSON 值；嵌套深度 ≤ maxDepth（根容器计为第 1 层，与 C# JsonDocument MaxDepth 语义对齐）；
//  3. 重复键容忍（成员按文档序保留，取值取首现——与 JsonElement.TryGetProperty 一致）；
//     重复/未知字段的判定由语义层按 C# 顺序精确复刻；
//  4. 数字形态白名单：仅接受整数字面量 -?0|[1-9][0-9]*，小数/指数在扫描层即拒绝；
//  5. 字符串非法转义/裸控制字符拒绝；
//  6. 产物为轻量 Node 树，语义层（parseHello 等）在其上取字段，
//     users.json / login / token payload 复用同一扫描器。
package protocol

import (
	"errors"
	"fmt"
	"unicode/utf16"
	"unicode/utf8"
)

type nodeKind uint8

const (
	kindObject nodeKind = iota
	kindArray
	kindString
	kindNumber
	kindBool
	kindNull
)

// Member 保留文档序与重复键。
type Member struct {
	Key string
	Val *Node
}

// Node 是扫描产物：轻量 JSON 树。
type Node struct {
	kind    nodeKind
	str     string   // kindString：解码后的值
	raw     string   // kindNumber：原始字面量
	boolean bool     // kindBool
	members []Member // kindObject
	items   []*Node  // kindArray
}

// Kind 返回节点类型名（诊断用）。
func (n *Node) Kind() string {
	switch n.kind {
	case kindObject:
		return "object"
	case kindArray:
		return "array"
	case kindString:
		return "string"
	case kindNumber:
		return "number"
	case kindBool:
		return "boolean"
	default:
		return "null"
	}
}

// IsObject / IsNull / IsArray / IsBool 类型判定。
func (n *Node) IsObject() bool { return n.kind == kindObject }
func (n *Node) IsNull() bool   { return n.kind == kindNull }
func (n *Node) IsArray() bool  { return n.kind == kindArray }
func (n *Node) IsBool() bool   { return n.kind == kindBool }
func (n *Node) IsString() bool { return n.kind == kindString }
func (n *Node) IsNumber() bool { return n.kind == kindNumber }

// Str 返回字符串值（仅 kindString 有意义）。
func (n *Node) Str() string { return n.str }

// Bool 返回布尔值（仅 kindBool 有意义）。
func (n *Node) Bool() bool { return n.boolean }

// RawNumber 返回数字原始字面量（仅 kindNumber 有意义）。
func (n *Node) RawNumber() string { return n.raw }

// Members 返回对象成员（文档序，可能含重复键）。
func (n *Node) Members() []Member { return n.members }

// Get 返回首个同名成员（与 JsonElement.TryGetProperty 一致）；不存在或非对象返回 nil。
func (n *Node) Get(key string) *Node {
	if n.kind != kindObject {
		return nil
	}
	for _, member := range n.members {
		if member.Key == key {
			return member.Val
		}
	}
	return nil
}

// Items 返回数组元素。
func (n *Node) Items() []*Node { return n.items }

// Len 返回对象成员数或数组元素数。
func (n *Node) Len() int {
	if n.kind == kindObject {
		return len(n.members)
	}
	if n.kind == kindArray {
		return len(n.items)
	}
	return 0
}

type jsonScanError struct{ msg string }

func (e *jsonScanError) Error() string { return e.msg }

func scanErrf(format string, args ...any) error {
	return &jsonScanError{msg: fmt.Sprintf(format, args...)}
}

// Decode 扫描 frame，返回根 Node；maxDepth 为最大嵌套深度（根容器 = 1）。
func Decode(frame []byte, maxDepth int) (*Node, error) {
	if !utf8.Valid(frame) {
		return nil, scanErrf("input is not valid UTF-8.")
	}
	p := &scanner{data: frame, maxDepth: maxDepth}
	p.skipWhitespace()
	if p.eof() {
		return nil, scanErrf("input is empty.")
	}
	root, err := p.parseValue(1)
	if err != nil {
		return nil, err
	}
	p.skipWhitespace()
	if !p.eof() {
		return nil, scanErrf("trailing content at position %d.", p.pos)
	}
	return root, nil
}

type scanner struct {
	data     []byte
	pos      int
	maxDepth int
}

func (p *scanner) eof() bool { return p.pos >= len(p.data) }

func (p *scanner) peek() byte { return p.data[p.pos] }

func (p *scanner) skipWhitespace() {
	for !p.eof() {
		switch p.data[p.pos] {
		case ' ', '\t', '\n', '\r':
			p.pos++
		default:
			return
		}
	}
}

func (p *scanner) parseValue(depth int) (*Node, error) {
	if p.eof() {
		return nil, scanErrf("unexpected end of input.")
	}
	switch c := p.peek(); {
	case c == '{':
		return p.parseObject(depth)
	case c == '[':
		return p.parseArray(depth)
	case c == '"':
		s, err := p.parseString()
		if err != nil {
			return nil, err
		}
		return &Node{kind: kindString, str: s}, nil
	case c == 't':
		if err := p.expectLiteral("true"); err != nil {
			return nil, err
		}
		return &Node{kind: kindBool, boolean: true}, nil
	case c == 'f':
		if err := p.expectLiteral("false"); err != nil {
			return nil, err
		}
		return &Node{kind: kindBool, boolean: false}, nil
	case c == 'n':
		if err := p.expectLiteral("null"); err != nil {
			return nil, err
		}
		return &Node{kind: kindNull}, nil
	case c == '-' || (c >= '0' && c <= '9'):
		return p.parseNumber()
	default:
		return nil, scanErrf("invalid character %q at position %d.", c, p.pos)
	}
}

func (p *scanner) expectLiteral(literal string) error {
	for i := 0; i < len(literal); i++ {
		if p.eof() || p.data[p.pos] != literal[i] {
			return scanErrf("invalid literal at position %d.", p.pos)
		}
		p.pos++
	}
	return nil
}

func (p *scanner) parseObject(depth int) (*Node, error) {
	if depth > p.maxDepth {
		return nil, scanErrf("maximum depth of %d exceeded.", p.maxDepth)
	}
	p.pos++ // '{'
	node := &Node{kind: kindObject}
	p.skipWhitespace()
	if !p.eof() && p.peek() == '}' {
		p.pos++
		return node, nil
	}
	for {
		p.skipWhitespace()
		if p.eof() || p.peek() != '"' {
			return nil, scanErrf("expected object key at position %d.", p.pos)
		}
		key, err := p.parseString()
		if err != nil {
			return nil, err
		}
		p.skipWhitespace()
		if p.eof() || p.peek() != ':' {
			return nil, scanErrf("expected ':' at position %d.", p.pos)
		}
		p.pos++
		p.skipWhitespace()
		value, err := p.parseValue(depth + 1)
		if err != nil {
			return nil, err
		}
		node.members = append(node.members, Member{Key: key, Val: value})
		p.skipWhitespace()
		if p.eof() {
			return nil, scanErrf("unexpected end of object.")
		}
		switch p.peek() {
		case ',':
			p.pos++
		case '}':
			p.pos++
			return node, nil
		default:
			return nil, scanErrf("unexpected character %q at position %d.", p.peek(), p.pos)
		}
	}
}

func (p *scanner) parseArray(depth int) (*Node, error) {
	if depth > p.maxDepth {
		return nil, scanErrf("maximum depth of %d exceeded.", p.maxDepth)
	}
	p.pos++ // '['
	node := &Node{kind: kindArray}
	p.skipWhitespace()
	if !p.eof() && p.peek() == ']' {
		p.pos++
		return node, nil
	}
	for {
		p.skipWhitespace()
		value, err := p.parseValue(depth + 1)
		if err != nil {
			return nil, err
		}
		node.items = append(node.items, value)
		p.skipWhitespace()
		if p.eof() {
			return nil, scanErrf("unexpected end of array.")
		}
		switch p.peek() {
		case ',':
			p.pos++
		case ']':
			p.pos++
			return node, nil
		default:
			return nil, scanErrf("unexpected character %q at position %d.", p.peek(), p.pos)
		}
	}
}

// parseNumber 只接受整数字面量 -?0|[1-9][0-9]*（小数点/指数形态直接拒绝）。
func (p *scanner) parseNumber() (*Node, error) {
	start := p.pos
	if p.peek() == '-' {
		p.pos++
		if p.eof() {
			return nil, scanErrf("unexpected end of number.")
		}
	}
	switch {
	case p.eof():
		return nil, scanErrf("unexpected end of number.")
	case p.peek() == '0':
		p.pos++
	case p.peek() >= '1' && p.peek() <= '9':
		for !p.eof() && p.peek() >= '0' && p.peek() <= '9' {
			p.pos++
		}
	default:
		return nil, scanErrf("invalid number at position %d.", start)
	}

	if !p.eof() {
		switch c := p.peek(); c {
		case '.', 'e', 'E':
			return nil, scanErrf("number must be an integer literal at position %d.", start)
		case '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-', '+':
			return nil, scanErrf("invalid number at position %d.", start)
		}
	}

	return &Node{kind: kindNumber, raw: string(p.data[start:p.pos])}, nil
}

// parseString 处理转义与 UTF-16 代理对；裸控制字符与非法转义拒绝。
func (p *scanner) parseString() (string, error) {
	p.pos++ // '"'
	var out []byte
	for {
		if p.eof() {
			return "", scanErrf("unterminated string.")
		}
		c := p.data[p.pos]
		switch {
		case c == '"':
			p.pos++
			return string(out), nil
		case c < 0x20:
			return "", scanErrf("control character in string at position %d.", p.pos)
		case c == '\\':
			p.pos++
			if p.eof() {
				return "", scanErrf("unterminated escape sequence.")
			}
			esc := p.data[p.pos]
			switch esc {
			case '"':
				out = append(out, '"')
				p.pos++
			case '\\':
				out = append(out, '\\')
				p.pos++
			case '/':
				out = append(out, '/')
				p.pos++
			case 'b':
				out = append(out, '\b')
				p.pos++
			case 'f':
				out = append(out, '\f')
				p.pos++
			case 'n':
				out = append(out, '\n')
				p.pos++
			case 'r':
				out = append(out, '\r')
				p.pos++
			case 't':
				out = append(out, '\t')
				p.pos++
			case 'u':
				if _, err := p.parseUnicodeEscape(&out); err != nil {
					return "", err
				}
			default:
				return "", scanErrf("invalid escape character %q at position %d.", esc, p.pos)
			}
		default:
			// 复制整个合法 UTF-8 序列（utf8.Valid 已保证整体合法）。
			r, size := utf8.DecodeRune(p.data[p.pos:])
			out = utf8.AppendRune(out, r)
			p.pos += size
		}
	}
}

func (p *scanner) parseUnicodeEscape(out *[]byte) (rune, error) {
	hi, err := p.parseHex4()
	if err != nil {
		return 0, err
	}
	if utf16.IsSurrogate(rune(hi)) {
		if p.pos+1 >= len(p.data) || p.data[p.pos] != '\\' || p.data[p.pos+1] != 'u' {
			return 0, scanErrf("unpaired surrogate in string at position %d.", p.pos)
		}
		p.pos++ // parseHex4 期望当前指向 'u'
		lo, err := p.parseHex4()
		if err != nil {
			return 0, err
		}
		combined := utf16.DecodeRune(rune(hi), rune(lo))
		if combined == utf8.RuneError {
			return 0, scanErrf("invalid surrogate pair in string at position %d.", p.pos)
		}
		*out = utf8.AppendRune(*out, combined)
		return combined, nil
	}
	*out = utf8.AppendRune(*out, rune(hi))
	return rune(hi), nil
}

func (p *scanner) parseHex4() (uint32, error) {
	if p.eof() || p.data[p.pos] != 'u' {
		return 0, scanErrf("invalid unicode escape at position %d.", p.pos)
	}
	p.pos++
	var value uint32
	for i := 0; i < 4; i++ {
		if p.eof() {
			return 0, scanErrf("invalid unicode escape at position %d.", p.pos)
		}
		c := p.data[p.pos]
		var digit uint32
		switch {
		case c >= '0' && c <= '9':
			digit = uint32(c - '0')
		case c >= 'a' && c <= 'f':
			digit = uint32(c-'a') + 10
		case c >= 'A' && c <= 'F':
			digit = uint32(c-'A') + 10
		default:
			return 0, scanErrf("invalid unicode escape at position %d.", p.pos)
		}
		value = value<<4 | digit
		p.pos++
	}
	return value, nil
}

// ErrScanSyntax 使扫描错误可被上游识别（如 login 固定报 "Invalid JSON."）。
func ErrScanSyntax(err error) bool {
	var target *jsonScanError
	return errors.As(err, &target)
}
