# MT播放器账号与同步服务：群晖部署

此服务只保存账号、设备和同步元数据，不接收、缓存或代理任何影视流。公网只需 Cloudflare Tunnel 的 HTTPS 域名，不需要群晖公网 IP，也不需要在路由器开放端口。

## 1. 准备配置

将整个源码目录放到群晖，然后进入 `deploy/synology`：

```sh
cp .env.example .env
```

编辑 `.env`，填写三个必填值。可在 PowerShell 生成：

```powershell
$bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
[Convert]::ToBase64String($bytes) # DATA_ENCRYPTION_KEY
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) # 其余随机口令
```

`DATA_ENCRYPTION_KEY` 用于加密后台保存的公开 API 地址和 SMTP 密码。该密钥丢失后无法解密原数据，务必离线备份；不要提交 `.env`。

## 2. 启动服务

```sh
docker compose up -d --build
docker compose ps
```

默认不会映射任何宿主机端口。PostgreSQL 也只在 `mtplayer` Docker 网络内可见。首次启动会自动执行数据库迁移。

## 3. 连接现有 Cloudflare Tunnel

推荐方式是在群晖 Container Manager 中，把现有 `cloudflared` 容器加入名为 `mtplayer` 的网络，然后将 Tunnel 的源站服务设置为：

```text
http://mt-api:8080
```

Cloudflare 公网域名不写入 Compose。域名确定后：

1. 浏览器访问 `https://你的域名/admin/setup`，使用 `.env` 中的 `ADMIN_SETUP_TOKEN` 创建首位管理员。
2. 登录 `/admin/settings`。
3. 在“公开 HTTPS 地址”中填写实际域名，例如 `https://api.example.com`。
4. 按需填写 SMTP；软件会在后台验证连通性，邮箱验证和密码找回随后即可启用。
5. 建议把 `.env` 的 `ALLOWED_HOSTS` 从 `*` 改成实际域名，再重启容器。

若 Tunnel 是直接安装在群晖系统中，无法加入 Docker 网络，可临时启用仅本机端口：

```sh
docker compose --profile nas-port up -d --build
```

此时源站为 `http://127.0.0.1:8888`。若确实需要让另一个容器通过 NAS 地址访问，请把 `MT_API_BIND_ADDRESS` 改为 NAS 的局域网地址，并用群晖防火墙限制来源；不要映射到公网地址。

## 4. 健康检查与日志

```sh
docker compose ps
docker compose logs --tail=200 mt-api
docker compose exec mt-api curl -fsS http://127.0.0.1:8080/health/live
docker compose exec mt-api curl -fsS http://127.0.0.1:8080/health/ready
```

- `/health/live`：进程是否存活。
- `/health/ready`：PostgreSQL 是否可访问、数据库迁移是否为最新。
- 每个响应均带 `X-Request-ID`，故障排查时可用它对应日志。

## 5. 更新与停止

```sh
docker compose up -d --build
docker compose down
```

`docker compose down` 不会删除 `mtplayer-postgres` 和 `mtplayer-data-protection` 卷。不要执行 `down -v`，除非明确要永久删除全部账号与同步数据。
