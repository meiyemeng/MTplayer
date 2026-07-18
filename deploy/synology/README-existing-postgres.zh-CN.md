# 使用群晖现有 PostgreSQL 部署 MT播放器服务

本方案只复用 PostgreSQL 容器，不复用 PMS 的数据库和账号。请在同一个 PostgreSQL 实例中新建独立的 `mtplayer` 数据库与 `mtplayer` 用户。

## 1. 导入镜像

在群晖 Container Manager 的“映像”页面，从文件导入 `MTPlayer-Server-Docker-amd64-1.3.0.tar.gz`。导入后的镜像名称应为：

```text
mtplayer/server:1.3.0-amd64
```

## 2. 新建独立数据库

先准备一个全新的随机密码，然后进入 PostgreSQL：

```sh
docker exec -it construction-pms-postgres-v3 psql -U pms -d postgres
```

在 `psql` 中执行，注意把密码替换成自己的值：

```sql
CREATE USER mtplayer WITH ENCRYPTED PASSWORD '请替换为随机密码';
CREATE DATABASE mtplayer OWNER mtplayer;
\q
```

不要修改或删除 `construction_pms` 数据库。

## 3. 准备项目

把以下两个文件放入群晖同一个目录，例如 `/volume1/docker/mtplayer`：

- `docker-compose.existing-postgres.yml`
- `.env.existing-postgres.example`

复制环境文件并编辑：

```sh
cd /volume1/docker/mtplayer
cp .env.existing-postgres.example .env
vi .env
```

`MTPLAYER_DATABASE_PASSWORD` 必须与上一步创建 `mtplayer` 用户时设置的密码一致。

如果 `pms_default` 不是实际网络名，可运行以下命令查看：

```sh
docker inspect construction-pms-postgres-v3 --format '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}}{{println}}{{end}}'
```

把输出的网络名称填写到 `.env` 的 `POSTGRES_DOCKER_NETWORK`。

## 4. 启动

```sh
docker compose -f docker-compose.existing-postgres.yml up -d
docker compose -f docker-compose.existing-postgres.yml ps
docker compose -f docker-compose.existing-postgres.yml logs --tail=200 mt-api
```

首次启动会自动在 `mtplayer` 数据库中建立表结构，不会操作 `construction_pms` 数据库。

## 5. Cloudflare Tunnel

把 `cloudflared` 容器加入 `mtplayer` 网络，Tunnel 源站填写：

```text
http://mt-api:8080
```

公网不需要开放 PostgreSQL 端口或 API 端口。
