# Wanxiang 0.4.0 Asset / Project 接口契约

命名说明：SOLIDWORKS 插件在本地把当前总装配体的完整导出目录称为 `assembly_package`（装配包）；本契约中的 `Project` 是 Wanxiang 远端实体。assembly_package 上传后映射为 Project，远端名称、路径和接口均不改名。

本文记录 SOLIDWORKS Asset Exporter 与 `wanxiang-data-service` main 分支的当前对接契约。
同步基准：`bd2476c6426e79b2e3b53d532fb71de3b6f71886`（2026-09-03）。

## 1. 固定远端布局

- Asset：`assets/<uuid>/v<version>/...`，只能通过资产原子发布接口写入。
- Project：由本地 assembly_package 上传而来，保存到 `projects/<assembly-uuid>/v<assembly-version>/...`，继续使用通用目录 ZIP 接口。
- Robot：不进入 `assets/`；仅在 Project XML 中写 `<mesh robot_id="设计目的:asset_version" />`。
- 客户端请求路径不再包含旧的共享目录前缀（例如 `wanxiang_test/`）。

所有请求使用：

```http
Authorization: Bearer <api-key>
```

插件禁用系统代理，避免开发机代理变量影响内网地址。

## 2. 分类预览：读取注册表

```http
GET /asset/registry
```

成功响应为注册表原始 JSON 字节，不增加 envelope：

```json
{
  "schema_version": "1.0",
  "assets": [
    {
      "asset_id": "<uuid>:1",
      "uuid": "<uuid>",
      "version": 1,
      "name": "底板",
      "relative_directory": "<uuid>/v1",
      "content_fingerprint": "<fingerprint>",
      "registered_utc": "2026-09-02T08:00:00.000000Z"
    }
  ]
}
```

响应头：

- `ETag`：响应体 SHA-256，带双引号；插件下载后必须校验。
- `X-Registry-Exists: true|false`：首次无注册表时仍返回 200 逻辑空表。
- `Cache-Control: no-cache`。

分类预览以本次远端注册表判断 Asset 版本；不以本地旧注册表作为版本权威。

## 3. Asset 原子发布

```http
PUT /asset/{uuid}/v{version}
Content-Type: application/zip
X-Content-Fingerprint: <manifest.content_fingerprint>

<版本目录 ZIP 原始字节>
```

这是 Asset 入库的唯一写入口。插件不得再执行以下旧流程：

- `PUT /archive/assets/<uuid>/v<N>`；
- `POST /asset/registry`；
- 通过 `/files` 或 `/archive` 写 `assets/asset-registry.json`。

ZIP 根目录必须直接包含：

```text
asset_<uuid>_v<version>.json
geometry/model.step
geometry/model.stl（大小写不敏感）
source/models/<至少一个源模型文件>
...manifest.files 声明的其他文件
```

ZIP 文件条目必须与 manifest `files` 完全一致，manifest 自身不写入 `files`。服务端逐项校验路径、大小和 SHA-256，并在同一发布事务中提交版本目录与注册表；任何失败都不留下可见的半发布目录。

### 3.1 manifest 必填约束

- `schema_version = "1.0"`；
- `uuid`、`version` 与 URL 一致；
- `content_fingerprint` 与请求头一致；
- `properties.零件名` 为非空字符串；
- `properties.设计目的` 为非空字符串；
- `properties.class` 必须精确为 `movable`、`structure`、`station` 之一；
- 旧拼写 `moveable`、大小写变化和首尾空格均不接受；
- `robot` 不通过本接口发布。

上述自定义属性检查必须在插件“分类预览”阶段完成并一次性汇总，不能等上传后才提示。

### 3.2 成功响应

新发布返回 201，幂等重试返回 200：

```json
{
  "status": "registered",
  "registration": {
    "asset_id": "<uuid>:1",
    "uuid": "<uuid>",
    "version": 1,
    "name": "底板",
    "relative_directory": "<uuid>/v1",
    "content_fingerprint": "<fingerprint>",
    "registered_utc": "2026-09-02T08:00:00.000000Z"
  }
}
```

- 201：`status=registered`；
- 200：`status=already_registered`；
- `Location: /asset/{uuid}/v{version}`；
- `ETag`：发布后的注册表 SHA-256。

插件必须核对响应中的 `asset_id`、`uuid`、`version`、`name`、`relative_directory`、`content_fingerprint` 和 `registered_utc`。

### 3.3 主要失败状态

- 400 `invalid_archive`：请求体不是有效 ZIP；
- 400 `path_violation`：ZIP 条目路径不安全；
- 409 `asset_manifest_mismatch`：manifest、ZIP、URL 或请求头不一致；
- 409 `asset_version_conflict`：同一版本内容或 manifest 不同；
- 409 `content_already_registered`：相同内容已注册到另一版本；
- 409 `asset_version_outdated`：版本不高于已注册最新版本；
- 422：UUID、版本或 `X-Content-Fingerprint` 不合法；
- 500：注册表、已发布 manifest 或服务端解压异常。

本地导出完成而上传失败时，插件保留本地目录，允许修复后原样重试。

## 4. Project 上传

Project 仍使用：

```http
PUT /archive/projects/<assembly-uuid>/v<assembly-version>
Content-Type: application/zip

<Project 目录 ZIP 原始字节>
```

关闭“导出 assembly_package”后，不生成本地装配包，也不上传 Wanxiang Project；普通非 Robot Asset 的原子发布仍照常执行。

## 5. 插件上传顺序

1. 分类预览读取 `GET /asset/registry` 并完成版本、属性和包前置检查；
2. 本地生成或复用所有非 Robot Asset 版本目录；
3. 逐项调用 `PUT /asset/{uuid}/v{version}` 原子发布 Asset；
4. 若启用 assembly_package 导出，将装配包作为 Wanxiang Project 调用 `PUT /archive/projects/...`；
5. 若启用本地注册表副本，再次调用 `GET /asset/registry`，校验 ETag 后保存。

Asset 逐项发布的 200/201 都是成功。若中途失败，已成功发布的版本保持有效；再次执行时由 200 幂等复用。

## 6. 验收要点

- 抓包确认 Asset 只有一个 PUT 请求，路径为 `/asset/<uuid>/v<N>`；
- 请求带 Bearer 和 `X-Content-Fingerprint`；
- 请求体 ZIP 根包含 manifest，且条目与 `files` 完全一致；
- 不出现 `PUT /archive/assets/...` 或 `POST /asset/registry`；
- Project 仍写入 `/archive/projects/...`；
- 201 与 200 都显示成功，409 保留本地包并显示服务端错误；
- `moveable`、空“零件名”、空“设计目的”在分类预览中一次性列出；
- Robot 不发布 Asset，Project XML 中使用 `robot_id="Hebe:1"` 形式。
