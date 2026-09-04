// TextCascade Server（Go 版，C# 1:1 迁移）。
// 动词分发：user → cli；serve → hosting.Run；其余参数原样交给 hosting.Run。
package main

import (
	"os"

	"github.com/long45343/TextCascade-Server/internal/cli"
	"github.com/long45343/TextCascade-Server/internal/hosting"
)

// version 由 release 构建注入：-ldflags "-X main.version=<tag>"。
var version = "dev"

func main() {
	args := os.Args[1:]

	if len(args) > 0 && args[0] == "user" {
		os.Exit(cli.Run(args, nil))
	}

	if len(args) > 0 && args[0] == "serve" {
		os.Exit(hosting.Run(args[1:]))
	}

	os.Exit(hosting.Run(args))
}
