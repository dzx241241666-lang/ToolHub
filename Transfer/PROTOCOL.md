# Switchboard Send / 1

独立局域网文件传输协议。Windows 和 Android 共用下述 JSON 字段；不是第三方客户端封装。

## 连接与发现

HTTPS，IPv4 私有地址、链路本地地址或回环地址。默认 TCP 53318，被占用时依次尝试至 53325；UDP 发现固定使用 53318，组播组 `224.0.0.167`、TTL 1，每 5 秒宣告。JSON 使用 UTF-8。

```json
{"kind":"switchboard-send","announce":true,"peer":{"id":"UUID","name":"电脑","platform":"Windows","port":53318,"certificateSha256":"64个十六进制字符","version":1}}
```

收到 `announce:true` 后回复 `announce:false`，自身 UUID 忽略。非手动设备 35 秒无消息后从列表消失。手动地址直接访问 HTTPS info。发现不提供经过认证的身份；设备名称可伪造。客户端首次读取 info 时比对响应内指纹与实际证书，之后连接固定该指纹，不跟随 HTTP 重定向、不使用代理。不向系统信任库添加证书。

## API

前缀 `/api/switchboard/v1`，HTTP/1.1，连接关闭式响应，上传与准备请求需提供准确 Content-Length。

| 请求 | 内容与响应 |
|---|---|
| GET /info | 返回上面的 peer 对象 |
| POST /prepare | `{sender:peer,files:[{id,name,size,mime,sha256}]}`，最多等待 120 秒的接收确认；成功返回 `{session:"随机会话",files:{"文件id":"随机凭据"}}` |
| POST /upload?session=…&file=…&token=… | 原始文件字节；完成、校验和发布成功后 200 |
| POST /cancel?session=… | 取消与来源地址相同的会话；幂等返回 200 |

JSON 元数据上限 256 KiB。每批 1–512 个文件，文件 ID 不重复，每个及每批上限 32 GiB。仅一个待确认或正在接收的批次；发送和接收可同时进行。文件名移除路径并净化，接收端选择目录，同名另存。Windows 写入本次专用临时文件，Android 使用 MediaStore 待发布条目；SHA-256 不符时不发布并清理该文件。

会话和逐文件 24 字节随机凭据由接收端生成，上传绑定准备请求的来源 IP。每个文件仅成功发布一次。拒绝 403、元数据或长度错误 400、忙碌/重复上传 409、大小超限 413、校验失败 422、空间不足 507。会话 3 分钟无活动过期。取消后已成功发布的文件保留，不支持续传。

“接收后复制”是本机接收偏好，不由发送方控制。整批成功后，本机将文件列表（Windows）或内容 URI（Android）交给剪贴板。

## 文本、剪贴板和文件夹（Windows 0.7 / Android 0.5）

传输接口和版本号保持不变，通过 `mime` 区分附加内容类型：

- `application/vnd.toolhub.text+utf8`：UTF-8 纯文本，最多 1 MiB。发送名称为 `.txt`；完整校验并严格验证 UTF-8 后发布。单独接收文本时，查看与复制均使用纯文本，接收剪贴板选项同样适用。
- `application/vnd.toolhub.folder+zip`：文件夹的 ZIP 容器，名称为原文件夹名加 `.zip`。接收后还原为独立文件夹，保留层级、空目录和空文件，已有同名文件夹另存。归档最多 10000 个条目，解压总量最多 32 GiB。绝对路径、父目录穿越、重复路径、不兼容文件名和链接条目不作为文件系统链接使用；Windows 拒绝链接条目，Android 仅通过用户授权的 DocumentsProvider 创建普通文件和目录。

Windows 先在独立临时目录完成文件夹还原，再移动到接收位置；Android 通过 SAF 创建独立目录，失败或取消时删除本次创建的目录。安卓首次接收文件夹需要选择并授权一个保存目录，后续复用；普通文件仍保存至 Downloads / ToolHub Send。

剪贴板读取仅在用户点击发送页的“剪贴板”时发生，不监控剪贴板。文字使用文本载荷；文件和文件夹复用对应载荷；Windows 位图转为 PNG，Android 图片使用剪贴板提供的内容 URI。

旧版客户端会将新载荷保存为普通 `.txt` 或 `.zip` 文件；自动文本查看、纯文本复制和文件夹还原需要两端更新。新版本仍支持旧客户端发送的普通文件。

## Android TLS identity

Android uses an ECDSA P-256 key restricted to signing/verification. Its digest authorizations include `NONE`: Conscrypt signs an already hashed digest when calling a non-exportable keystore key. Version 0.4.0's RSA identity omitted the raw operation authorizations needed by Conscrypt, which could terminate TLS handshakes before an HTTP request reached the app. The new alias leaves the old app-owned identity untouched and advertises the new certificate fingerprint. The desktop peer list includes that fingerprint when deciding whether to update a card.

Primary implementation reference: [Conscrypt CryptoUpcalls](https://github.com/google/conscrypt/blob/master/common/src/main/java/org/conscrypt/CryptoUpcalls.java), [Conscrypt issue 1201](https://github.com/google/conscrypt/issues/1201). The listening service performs an actual pinned local HTTPS request before reporting ready; it does not rely only on successful key generation.
