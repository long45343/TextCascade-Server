# syntax=docker/dockerfile:1
#
# TextCascade Server — 运行镜像。
#
# 构建阶段固定在 BUILDPLATFORM 上做原生交叉编译（Go 交叉编译不需要 QEMU），
# 因此多架构构建不会退化成模拟执行；运行阶段才切到 TARGETPLATFORM。
#   构建：docker build -t textcascade-server .
#   版本：--build-arg VERSION=1.0.0
#
# 运行时基底为 distroless static：无 shell、无包管理器，进程以 UID 65532 运行。
# 容器内无法执行 shell 脚本，用户管理请用
#   docker exec <container> /TextCascade.Server user ...
ARG GO_IMAGE=golang:1.27-alpine
FROM --platform=$BUILDPLATFORM ${GO_IMAGE} AS build

ARG TARGETOS
ARG TARGETARCH
ARG VERSION=dev

WORKDIR /src

# 先只拷依赖清单，让 go mod download 层在源码变化时仍可复用。
COPY go.mod go.sum ./
RUN go mod download

COPY cmd ./cmd
COPY internal ./internal

RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} \
    go build -trimpath -ldflags "-s -w -X main.version=${VERSION}" \
    -o /out/TextCascade.Server ./cmd/server

# distroless 没有 mkdir，预建运行期目录树后整棵拷入，使其携带 nonroot 属主。
# 这些目录同时是标准挂载点：首次挂载卷时会继承此处属主。
RUN mkdir -p /out/rootfs/etc/textcascade /out/rootfs/var/lib/textcascade \
    && touch /out/rootfs/etc/textcascade/.keep /out/rootfs/var/lib/textcascade/.keep

FROM --platform=$TARGETPLATFORM gcr.io/distroless/static-debian12:nonroot

ARG VERSION=dev

LABEL org.opencontainers.image.title="TextCascade Server" \
      org.opencontainers.image.description="Lightweight clipboard synchronization server" \
      org.opencontainers.image.licenses="GPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.source="https://github.com/long45343/TextCascade-Server"

WORKDIR /

COPY --from=build /out/TextCascade.Server /TextCascade.Server

# 运行期目录：/etc/textcascade（配置、users.json、证书）与 /var/lib/textcascade（状态）。
COPY --from=build --chown=65532:65532 /out/rootfs/ /

# 默认配置放在 /opt/textcascade：/etc/textcascade 是常规 bind 挂载点，
# 放在那里一旦挂载就会被宿主目录整体遮蔽。
COPY --chown=65532:65532 deploy/docker/textcascade.toml /opt/textcascade/textcascade.toml

USER 65532:65532

EXPOSE 8443

# 默认指向镜像内配置，使裸 docker run 也能启动；
# compose.yaml 通过 command 覆盖为宿主挂载的 /etc/textcascade/textcascade.toml。
ENTRYPOINT ["/TextCascade.Server"]
CMD ["serve", "--config", "/opt/textcascade/textcascade.toml"]
