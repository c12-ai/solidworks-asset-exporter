# Wanxiang 资产交互 API 需求文档

> 状态：P0 已实现并由 Exporter v1.0.4 接入；纯元数据 Robot 例外仍需服务端实现  
> 日期：2026-09-02  
> 参考服务：[c12-ai/wanxiang-data-service](https://github.com/c12-ai/wanxiang-data-service)，`main@cf3f075e72b879f9701381795296a2a66eaae695`  
> 首个调用方：SolidWorks Asset Exporter

## 1. 背景

Wanxiang Data Service 当前提供通用文件能力：

- `GET|HEAD /files/{path}`：下载或探测单文件；
- `PUT /files/{path}`：原子覆盖单文件；
- `GET /list/{path}`：列目录；
- `GET|PUT /archive/{path}`：以 ZIP 下载目录或合并上传目录；
- `POST /search/assets`：按自然语言搜索资产 manifest。

这些接口能完成资产上传和读取，但调用方必须了解物理目录、manifest 文件名和注册表文件名。当前 SolidWorks Asset Exporter 注册资产时还需要执行“下载整个 `asset-registry.json` → 本地合并 → 覆盖上传”。两个客户端并发执行时，后写入者可能覆盖先写入者新增的条目。

本需求增加稳定的资产领域 API，由服务端负责路径映射、数据校验、幂等、冲突检测和注册表原子更新。

## 2. 目标与范围

### 2.1 P0 目标

1. 通过 `GET /asset/{uuid}/v{version}` 直接取得指定资产版本的 manifest JSON 内容。
2. 通过 `GET /asset/registry` 直接取得资产注册表 JSON 内容。
3. 通过 `POST /asset/registry` 原子注册一个已经上传的资产版本，不再由客户端覆盖整个注册表。
4. 同一 `uuid + version` 一经注册即不可被另一份内容覆盖。
5. 保持现有通用文件 API 和 `POST /search/assets` 向后兼容。

### 2.2 非目标

- 本期不删除资产版本；
- 本期不修改已经注册的 manifest 或资产文件；
- 本期不替代现有 ZIP 上传接口；
- 本期不设计用户、角色和租户模型，继续使用现有 Bearer API key 鉴权。

## 3. 术语与存储约定

### 3.1 逻辑结构

```text
<asset-root>/
  asset-registry.json
  <uuid>/
    v<version>/
      asset_<uuid>_v<version>.json
      # 非 Robot：
      geometry/model.step
      geometry/model.stl
      source/models/...  # SLDASM/SLDPRT/SLDDRW
      # class=robot：目录中只有 manifest，manifest.files=[]
```

当前插件通过通用文件 API 时使用固定逻辑目录：Asset 与注册表位于 `assets`，Project 位于 `projects`；这两个目录不接受界面或配置覆盖。未来切换到资产领域 API 后，`<asset-root>` 仍应由服务端配置项决定，并对客户端隐藏实际文件系统路径。

### 3.2 标识规则

- `uuid`：RFC 4122 UUID；服务端统一输出小写、带连字符的 canonical form，例如 `123e4567-e89b-12d3-a456-426614174000`。
- `version`：从 `1` 开始的十进制正整数。
- `asset_id`：`<canonical-uuid>:<version>`。
- `relative_directory`：`<canonical-uuid>/v<version>`，由服务端生成，客户端不得指定。
- manifest 文件名：`asset_<canonical-uuid>_v<version>.json`。

### 3.3 Manifest 最小契约

```json
{
  "schema_version": "1.0",
  "uuid": "123e4567-e89b-12d3-a456-426614174000",
  "version": 2,
  "content_fingerprint": "sha256-or-project-defined-fingerprint",
  "properties": {
    "class": "equipment",
    "设计目的": "用于示例"
  },
  "files": [
    {
      "path": "geometry/model.step",
      "sha256": "...",
      "size": 123456
    }
  ]
}
```

注册时至少校验 `schema_version`、`uuid`、`version`、`content_fingerprint` 和 `files` 的类型及必填值。manifest 内的 `uuid`、`version` 必须与 API 路径一致。

### 3.4 注册表契约

```json
{
  "schema_version": "1.0",
  "assets": [
    {
      "asset_id": "123e4567-e89b-12d3-a456-426614174000:2",
      "uuid": "123e4567-e89b-12d3-a456-426614174000",
      "version": 2,
      "relative_directory": "123e4567-e89b-12d3-a456-426614174000/v2",
      "content_fingerprint": "sha256-or-project-defined-fingerprint",
      "registered_utc": "2026-09-01T12:34:56.789Z"
    }
  ]
}
```

注册表要求：

- `schema_version` 固定为 `1.0`；
- `asset_id` 全局唯一；
- 条目按 `asset_id` 不区分大小写排序，保证输出稳定；
- `relative_directory` 和 `registered_utc` 由服务端生成；
- `registered_utc` 使用 UTC ISO 8601；幂等重试不得刷新已有条目的时间。

## 4. 通用 API 约定

### 4.1 鉴权

除健康检查外，全部接口复用现有鉴权方式：

```http
Authorization: Bearer <api-key>
```

缺失、格式错误或无效 key 返回 `401 unauthorized`。日志只记录映射后的 caller 名称，不记录 API key。

### 4.2 内容类型和编码

- JSON 请求：`Content-Type: application/json`；
- JSON 响应：`Content-Type: application/json; charset=utf-8`；
- JSON 文件按 UTF-8 读取；不接受 BOM 之外的非 UTF-8 编码；
- manifest 和注册表读取成功时直接返回文件对应的 JSON 对象，不增加 `data` 包装层。

### 4.3 缓存与追踪

- manifest 是不可变版本资源，成功响应应提供强 `ETag`，值基于响应 JSON 文件的原始字节计算；
- 注册表是可变资源，成功响应应提供 `ETag`，并设置 `Cache-Control: no-cache`；
- P1 支持 `If-None-Match`，命中时返回 `304`；
- 服务端应接受或生成 `X-Request-ID`，并在响应和结构化日志中返回同一值。

### 4.4 错误格式

除现有鉴权依赖产生的 `401` 包装格式外，新接口沿用 Wanxiang Data Service 的 `ErrorResponse`：

```json
{
  "error": "asset_not_found",
  "message": "Asset manifest was not found.",
  "field": null,
  "details": {
    "uuid": "123e4567-e89b-12d3-a456-426614174000",
    "version": 2
  }
}
```

不得在错误响应中暴露服务端绝对文件路径、API key、堆栈或内部锁文件名。现有 `401` 为 `{"detail": {<ErrorResponse>}}`；建议后续在大版本中统一成顶层错误对象，但本期不为新增接口破坏现有客户端兼容性。

## 5. P0 API 详细需求

### 5.1 获取指定资产版本 Manifest

```http
GET /asset/{uuid}/v{version}
```

#### 请求参数

| 参数 | 位置 | 类型 | 必填 | 规则 |
|---|---|---:|---:|---|
| `uuid` | path | UUID | 是 | 接受 UUID 大小写输入，内部转 canonical form |
| `version` | path | integer | 是 | `>= 1`，路径中包含固定前缀 `v` |

#### 服务端行为

1. 校验并规范化 `uuid` 和 `version`；
2. 仅由服务端拼接 manifest 路径，不把原始路径参数当作任意文件路径使用；
3. 读取 `<asset-root>/<uuid>/v<version>/asset_<uuid>_v<version>.json`；
4. 校验文件为合法 JSON 对象，且其中的 `uuid`、`version` 与请求一致；
5. 直接返回 manifest JSON 内容，不增加响应 envelope。

#### 成功响应

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
ETag: "<sha256-of-response-bytes>"
X-Asset-Id: 123e4567-e89b-12d3-a456-426614174000:2
```

Body 为第 3.3 节定义的 manifest JSON。

#### 状态码

| 状态码 | `error` | 条件 |
|---:|---|---|
| 200 | — | manifest 存在且有效 |
| 401 | `unauthorized` | 未通过鉴权 |
| 404 | `asset_not_found` | 版本目录或 manifest 不存在 |
| 422 | — | UUID 格式错误或版本不是正整数 |
| 500 | `asset_manifest_invalid` | 文件存在但 JSON 损坏，或文件内身份与路径不一致 |

### 5.2 获取资产注册表

```http
GET /asset/registry
```

#### 服务端行为

1. 读取 `<asset-root>/asset-registry.json`；
2. 校验顶层对象、`schema_version`、条目身份和唯一性；
3. 直接返回注册表 JSON 内容，不增加响应 envelope；
4. 新部署尚未创建注册表时返回逻辑空注册表，而不是 `404`：

```json
{
  "schema_version": "1.0",
  "assets": []
}
```

空注册表响应增加 `X-Registry-Exists: false`；真实文件响应为 `true`。这样客户端无须为首次注册维护另一条初始化流程。

#### 成功响应

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Cache-Control: no-cache
ETag: "<registry-content-sha256>"
X-Registry-Exists: true
```

#### 状态码

| 状态码 | `error` | 条件 |
|---:|---|---|
| 200 | — | 返回真实或逻辑空注册表 |
| 401 | `unauthorized` | 未通过鉴权 |
| 500 | `asset_registry_invalid` | 注册表文件存在但无法解析或违反 schema |

### 5.3 注册资产版本

```http
POST /asset/registry
Content-Type: application/json
```

#### 请求体

```json
{
  "uuid": "123e4567-e89b-12d3-a456-426614174000",
  "version": 2,
  "content_fingerprint": "sha256-or-project-defined-fingerprint"
}
```

| 字段 | 类型 | 必填 | 规则 |
|---|---:|---:|---|
| `uuid` | UUID string | 是 | 服务端规范化 |
| `version` | integer | 是 | `>= 1` |
| `content_fingerprint` | string | 是 | 非空；必须与磁盘 manifest 一致 |

客户端不得提交 `asset_id`、`relative_directory` 或 `registered_utc`，这些字段全部由服务端生成。

#### 前置条件

- 资产版本目录及 manifest 必须已经通过现有 `PUT /archive/{path}` 上传成功；
- manifest 的 UUID、版本和内容指纹必须与请求一致；
- 非 Robot manifest 至少声明 `geometry/model.step`、`geometry/model.stl` 和一个 `source/models/` 下的文件；
- `manifest.properties.class` 忽略大小写及首尾空格等于 `robot` 时必须作为纯元数据 Asset：允许且要求 `files=[]`，不得要求或生成 STEP、STL、SLDASM、SLDPRT、SLDDRW；当前 Wanxiang `main@cf3f075e72b879f9701381795296a2a66eaae695` 的 `_check_required_files` 仍无条件要求几何和源目录，部署端必须按此例外更新后才能注册 Exporter v1.0.4 的 Robot；
- manifest `files` 中声明的每个文件必须存在于版本目录内；P0 至少校验路径和大小，P1 增加全部 SHA-256 校验。

#### 原子注册流程

服务端必须把以下操作置于同一互斥区内：

1. 取得跨线程、跨进程的注册表写锁；若多实例部署，锁必须覆盖全部写入实例；
2. 在锁内重新读取最新注册表；
3. 校验现有条目、目标 manifest 和版本策略；
4. 生成新条目并按 `asset_id` 排序；
5. 写入同目录临时文件，刷盘成功后通过原子 replace 替换注册表；
6. 释放锁并返回结果。

仅使用进程内 `asyncio.Lock` 不满足多 worker 或多实例并发要求。若底层 NAS 不保证文件锁和同目录原子 rename 语义，应改用数据库事务或分布式锁，并把 JSON 注册表作为事务后的投影文件。

#### 幂等和冲突规则

| 场景 | 结果 |
|---|---|
| `asset_id` 不存在，且版本策略通过 | 新增条目，返回 `201` |
| `asset_id` 已存在，且 `content_fingerprint` 相同 | 幂等成功，返回 `200`，不修改条目和 `registered_utc` |
| `asset_id` 已存在，但指纹不同 | `409 asset_version_conflict`，禁止覆盖 |
| 同一 UUID 的相同指纹已注册到其他版本 | `409 content_already_registered`，返回已有版本 |
| 请求版本低于或等于该 UUID 的最新版本，但不是已有幂等条目 | `409 asset_version_outdated`，返回最新版本和建议版本 |
| manifest 不存在 | `404 asset_not_found` |
| manifest 身份、指纹或文件清单不匹配 | `409 asset_manifest_mismatch` |

#### 新增成功响应

```http
HTTP/1.1 201 Created
Location: /asset/123e4567-e89b-12d3-a456-426614174000/v2
ETag: "<new-registry-content-sha256>"
```

```json
{
  "status": "registered",
  "registration": {
    "asset_id": "123e4567-e89b-12d3-a456-426614174000:2",
    "uuid": "123e4567-e89b-12d3-a456-426614174000",
    "version": 2,
    "relative_directory": "123e4567-e89b-12d3-a456-426614174000/v2",
    "content_fingerprint": "sha256-or-project-defined-fingerprint",
    "registered_utc": "2026-09-01T12:34:56.789Z"
  }
}
```

#### 幂等成功响应

与新增响应结构相同，但状态码为 `200`，`status` 为 `already_registered`。

#### 冲突响应示例

```http
HTTP/1.1 409 Conflict
```

```json
{
  "error": "asset_version_conflict",
  "message": "The asset version is already registered with different content.",
  "field": "content_fingerprint",
  "details": {
    "asset_id": "123e4567-e89b-12d3-a456-426614174000:2",
    "registered_fingerprint": "existing-value"
  }
}
```

服务端不得在冲突时返回或记录请求中的敏感数据；内容指纹不是密钥，可以作为诊断字段返回。

## 6. 客户端交互流程

### 6.1 分类预览

1. `GET /asset/registry`；
2. 按相同 UUID、版本和 `content_fingerprint` 判断复用、升级或冲突；
3. 可缓存响应 `ETag`，但不把它作为注册成功的必要条件，避免无关资产的并发注册阻断本次操作。

### 6.2 导出并注册

1. 本地生成并校验 Asset 版本目录；
2. 使用现有 `PUT /archive/{physical-asset-path}` 上传版本目录；
3. 如有 Project，继续使用现有 `PUT /archive/{project-path}` 上传；
4. 对每个成功上传的 Asset 调用 `POST /asset/registry`；
5. 对网络超时或连接中断，可原样重试第 4 步；注册 API 必须幂等；
6. `409` 不自动覆盖或自动提升版本，客户端提示用户重新预览或提升 `asset_version`。

新流程不再允许客户端通过 `PUT /files/.../asset-registry.json` 覆盖注册表。建议为普通写入 key 禁止对注册表物理路径执行通用 `PUT /files`，只保留管理员恢复权限，避免绕过服务端注册规则。

## 7. 建议增加的其他 API

以下按优先级排序。

### 7.1 P1：存在性检查与条件读取

```http
HEAD /asset/{uuid}/v{version}
HEAD /asset/registry
```

返回与对应 `GET` 相同的状态码、`Content-Length`、`ETag` 等响应头，但不返回 body。`GET` 同时支持 `If-None-Match`。这与现有 `/files` 的 HEAD 能力一致，可用于低成本探测和客户端缓存更新。

### 7.2 P1：下载完整资产版本

```http
GET /asset/{uuid}/v{version}/archive
```

返回 `application/zip`，语义与现有 `GET /archive/{path}` 相同，但不暴露物理目录。对于仿真、离线缓存和跨环境同步，这是读取 manifest 之后最常见的下一步。

### 7.3 P1：获取版本列表和最新版本

```http
GET /asset/{uuid}/versions
GET /asset/{uuid}/latest
```

`versions` 返回已注册版本、指纹、注册时间和 `latest_version`；`latest` 返回最新 manifest，并通过 `Content-Location` 指向不可变版本 URL。不要让业务客户端通过列物理目录推断最新版本。

### 7.4 P1：资产发布事务

```http
POST /asset/{uuid}/v{version}/publish
Content-Type: application/zip
```

把“上传 ZIP、完整校验、落盘、注册”合并为一次服务端工作流。成功前先写 staging，全部通过后再提交版本目录和注册表，可消除“文件已上传但未注册”的孤儿版本。该接口完成后可取代第 6.2 节中的两步上传与注册流程。

### 7.5 P1：批量原子注册

```http
POST /asset/registry/batch
```

一次提交多个 `{uuid, version, content_fingerprint}`，在同一注册表锁和事务中全部校验、全部成功或全部失败。装配导出一次产生多个新 Asset 时，可以避免逐条注册导致的部分成功，并减少反复读取和改写注册表。响应应逐项返回结果；任一项冲突时整体返回 `409`，不得写入任何新条目。

### 7.6 P1：读取资产内单个文件

```http
GET /asset/{uuid}/v{version}/files/{relative_path}
HEAD /asset/{uuid}/v{version}/files/{relative_path}
```

用于只下载 STEP、STL、SLDDRW 等单个文件。`relative_path` 必须经过现有文件服务同等级别的目录穿越、绝对路径、反斜杠、隐藏路径段和符号链接逃逸校验，并且限制在目标版本目录内。

### 7.7 P1：确定性资产查询

```http
GET /assets?class=equipment&latest_only=true&cursor=...&limit=50
```

现有 `POST /search/assets` 依赖 LLM，适合自然语言搜索但不适合稳定过滤、分页和高频轮询。建议补充不调用 LLM 的结构化查询接口，支持 `uuid`、`asset_id`、`class`、属性、版本和注册时间过滤。

### 7.8 P2：批量解析

```http
POST /assets/resolve
```

请求一组 `asset_id`，批量返回 manifest 摘要、下载 URL、缺失项和冲突项。装配体一次引用多个 Asset 时可显著减少往返次数。

### 7.9 P2：增量注册表或变更流

```http
GET /asset/registry/changes?after=<cursor>&limit=500
```

当注册表增长后，客户端无需反复下载完整 JSON。cursor 必须是服务端生成的不透明值；返回稳定顺序和下一页 cursor。完整 `GET /asset/registry` 仍作为快照接口保留。

### 7.10 P2：资产完整性检查

```http
GET /asset/{uuid}/v{version}/validation
```

返回 manifest schema、必需文件、大小和 SHA-256 校验结果。适合发布前检查、定时巡检和排查 NAS 文件损坏；检查过程为只读，不自动修复。

### 7.11 P2：弃用状态，而非物理删除

```http
PATCH /asset/{uuid}/v{version}/status
```

建议支持 `active`、`deprecated`、`withdrawn`，并记录原因和操作者。默认查询不返回 `withdrawn`，但历史装配仍能按精确版本解析。除管理员离线维护外，不建议提供通用 `DELETE`，以保护装配体引用的可重复性。

## 8. 安全、并发与可靠性要求

- 所有路径由 canonical UUID 和正整数版本构造；不得把任意用户路径直接拼接到 `<asset-root>`；
- 路径解析后必须再次确认位于 `<asset-root>` 内，防止符号链接逃逸；
- 注册请求体设置合理大小上限，例如 64 KiB；
- 注册表写入必须是锁内重新读取、校验、临时文件写入和原子替换；
- 服务重启、请求取消或磁盘写入失败后，旧注册表必须仍然完整可读；
- 注册表临时文件使用 `.` 前缀，并沿用当前服务的隐藏文件规则；
- 资产版本不可变：已注册版本的通用文件写入应被 ACL 或服务端规则拒绝；
- 若仍允许通过通用 `/files`、`/archive` 写入资产树，至少禁止覆盖已注册版本；
- 不以客户端时间生成 `registered_utc`；
- 不在日志中记录 Bearer token、整个注册表或大体积 manifest；
- 对同一 `asset_id` 的高并发相同请求只能生成一个条目，所有请求最终得到 `201` 或幂等 `200`；
- 对不同 `asset_id` 的并发注册不得丢失任何已成功响应的条目。

## 9. 可观测性要求

每次注册记录一条结构化审计日志，至少包含：

- `request_id`；
- `caller`；
- `operation=asset_register`；
- `asset_id`；
- `result=registered|already_registered|conflict|failed`；
- `duration_ms`；
- 错误代码（若有）。

建议暴露以下指标：

- 注册请求数及按结果分类的计数；
- 注册锁等待时间；
- 注册表写入耗时与失败数；
- manifest 读取失败数；
- 注册表条目总数；
- 完整性校验失败数。

## 10. 兼容与迁移

1. 现有 `/files`、`/archive`、`/list` 和 `/search/assets` 不改路径和响应；
2. 新 API 使用服务端 `assets_dir` 映射物理目录，不要求旧客户端立刻迁移；
3. SolidWorks Asset Exporter 先改为读取 `GET /asset/registry`、写入 `POST /asset/registry`；
4. 上传仍可暂用 `PUT /archive`，之后再迁移到 `publish`；
5. 新注册 API 稳定运行后，禁止普通 key 直接覆盖 `asset-registry.json`；
6. 发布前扫描现有注册表，确认无重复 `asset_id`、身份错配和无对应 manifest 的条目；
7. OpenAPI 文档应标注新增接口为资产领域接口，并给出可复制的请求示例。

## 11. 验收标准

### 11.1 Manifest 获取

- 合法 UUID 和版本返回对应 manifest JSON，且没有额外包装层；
- UUID 大写输入可正确解析，响应中的 UUID 为 canonical form；
- 不存在返回 `404 asset_not_found`；
- 损坏 JSON 或身份错配返回 `500 asset_manifest_invalid`；
- 响应 `Content-Type` 为 JSON，而不是现有通用文件接口的 `application/octet-stream`。

### 11.2 注册表获取

- 文件存在时返回其 JSON 内容和 `ETag`；
- 文件不存在时返回 `200` 空注册表及 `X-Registry-Exists: false`；
- 重复 `GET` 不修改注册表时间戳或文件内容；
- 注册表损坏时不静默返回空表，而是返回 `500 asset_registry_invalid`。

### 11.3 资产注册

- 新条目返回 `201 registered`；
- 相同请求重试返回 `200 already_registered`，注册表仍只有一个条目；
- 同一 `asset_id` 不同指纹返回 `409`，原条目不变；
- 指纹、UUID 或版本与 manifest 不一致时返回 `409`；
- manifest 或声明文件缺失时注册失败；
- 20 个并发请求注册 20 个不同资产，最终注册表保留全部 20 个条目；
- 20 个并发请求注册同一资产，最终只有一个条目且内容一致；
- 模拟写盘失败后，旧注册表仍可解析且不包含半条记录；
- 并发读取只能看到完整旧版本或完整新版本，不能看到部分 JSON；
- 未授权请求全部返回 `401`，且不改变文件系统。

## 12. 建议实现边界

在 Wanxiang Data Service 中建议新增：

```text
app/api/routers/assets.py       # HTTP 参数、状态码和响应头
app/services/asset_service.py  # 路径映射、读取、校验、注册与锁
```

Pydantic 请求/响应模型放入现有 `app/api/schemas.py` 或按领域拆分。路由层保持薄，只负责绑定参数并调用 service；文件路径校验和异常转换复用现有 `file_service` 与全局异常处理机制。

首期实现顺序建议为：

1. 统一并配置 `<asset-root>`；
2. 实现 manifest/registry 读取和 schema 校验；
3. 实现可跨进程的原子注册；
4. 增加并发、故障注入、鉴权和 OpenAPI 测试；
5. 修改 SolidWorks Asset Exporter，移除客户端注册表覆盖写入；
6. 收紧普通 key 对注册表及已注册版本的通用写权限。
