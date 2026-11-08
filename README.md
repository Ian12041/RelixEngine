# ReliX Engine

基於 .NET 8 與 MS SQL Server 實作的分散式非同步任務與交易一致性引擎。本專案旨在解決跨系統整合時常見的「雙寫資料不一致（Dual-Write Hazard）」、網路重試引發的重複處理，以及下游服務異常時的容錯恢復問題。

## 核心設計決策與權衡 (Architecture & Trade-offs)

在分散式環境下，跨資料庫與訊息佇列（Message Broker）的資料一致性是常見痛點。本專案放棄效能損耗過高且易引發鎖競爭的兩階段提交（2PC / XA Transaction），改採以下設計組合：

1. **Transactional Outbox Pattern（交易外發箱模式）**
   * **實作方式**：在同一個本地資料庫交易（`SqlTransaction`）邊界內，同步寫入業務主表（`Orders`）與事件外發箱表（`OutboxMessages`）。
   * **權衡取捨**：犧牲了部分即時性（引入背景輪詢或 CDC 的微秒級延遲），換取了資料庫層級的 ACID 原子性，確保業務資料落地與事件發佈「同生共死」，徹底消除寫完 DB 但 MQ 斷線造成的事件遺失。
2. **資料存取層選用 Dapper 而非 EF Core**
   * **原因**：針對高吞吐與明確交易邊界的場景，Dapper 能提供接近原生 ADO.NET 的執行效率，並讓開發者完全掌控 SQL 執行計畫、連線生命週期與交易鎖定範圍，避免 ORM 隱式查詢或追蹤狀態帶來的額外開銷。
3. **非同步削峰與 At-Least-Once 語義**
   * Outbox Publisher 僅保證訊息「至少送達一次（At-Least-Once）」，網路抖動時可能發送重複事件。因此將去重責任下放至 API 入口與下游 Consumer，透過唯一鍵（Unique Constraint）與狀態機實現端到端冪等性（Idempotency）。

## 系統架構與資料流

```text
Client Request (REST API)
       │
       ▼
[OrdersController] ──▶ (Phase 2: Idempotency-Key 攔截驗證)
       │
       ▼
[OrderService] (開啟單一 SqlTransaction)
       ├──▶ INSERT INTO Orders (業務單據落地)
       └──▶ INSERT INTO OutboxMessages (寫入 PENDING 事件)
       │
       ▼ (Transaction Commit)
[MS SQL Server]
       │
       ▼ (Phase 3: 非同步背景輪詢 / 索引掃描)
[OutboxPublisherWorker] ──▶ 推播至外部端點 / Message Broker
       │
       └──▶ 失敗時觸發 Exponential Backoff 重試，超限轉入 DLQ
```

## 資料庫結構設計 (Database Schema)

所有資料表結構定義於初始化腳本，針對高頻輪詢與併發寫入進行索引設計：

* **`Orders`**：核心業務訂單表。內建 `ROWVERSION` 欄位，為後續多終端併發更新提供樂觀鎖（Optimistic Concurrency Control）版本檢核。
* **`OutboxMessages`**：事件外發箱表。建立複合索引 `IX_OutboxMessages_Status_CreatedAt (Status, CreatedAt)`，確保背景 Worker 輪詢 `Status = 'PENDING'` 時僅進行 Index Seek，避免全表掃描影響線上業務寫入效能。
* **`IdempotencyRecords`**：冪等性控制表。以 `IdempotencyKey` 作為 Clustered Primary Key，利用資料庫物理唯一性約束阻擋高併發下的重複請求穿透。

## 本地開發環境建置 (Local Setup)

### 環境需求
* .NET 8.0 SDK
* MS SQL Server 2019+ (本機執行個體或 Docker 容器)

### 1. 初始化資料庫
連線至本地 SQL Server 執行個體，建立 `RelixDb` 並建立核心資料表：

```sql
CREATE DATABASE RelixDb;
GO
USE RelixDb;
GO

CREATE TABLE IdempotencyRecords (
    IdempotencyKey VARCHAR(64) NOT NULL PRIMARY KEY,
    RequestPath NVARCHAR(256) NOT NULL,
    Status VARCHAR(20) NOT NULL,
    ResponseStatusCode INT NULL,
    ResponseBody NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE Orders (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    OrderNo VARCHAR(50) NOT NULL UNIQUE,
    CustomerName NVARCHAR(100) NOT NULL,
    Amount DECIMAL(18, 2) NOT NULL,
    Status VARCHAR(20) NOT NULL DEFAULT 'CREATED',
    RowVersion ROWVERSION NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE OutboxMessages (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    EventId UNIQUEIDENTIFIER NOT NULL,
    EventType VARCHAR(100) NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    Status VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    RetryCount INT NOT NULL DEFAULT 0,
    LastError NVARCHAR(MAX) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedAt DATETIME2 NULL
);

CREATE INDEX IX_OutboxMessages_Status_CreatedAt ON OutboxMessages(Status, CreatedAt);
GO
```

### 2. 設定連線字串
在 `Relix.Api/` 目錄下建立 `appsettings.Development.json`（此檔案已納入 `.gitignore` 避免敏感資訊進入版控）：

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=.\\MSSQLSERVER01;Database=RelixDb;User Id=sa;Password=<YOUR_PASSWORD>;TrustServerCertificate=True;"
  }
}
```

### 3. 啟動服務
```bash
dotnet restore
dotnet run --project Relix.Api/Relix.Api.csproj
```
啟動後可透過終端機顯示之本機埠號訪問 `/swagger` 進行 API 測試。

## API 介面規格

### 建立訂單 (`POST /api/v1/orders`)
寫入新訂單並同步產生 `OrderCreated` 外發事件。

* **Request Body**:
```json
{
  "customerName": "台新銀行採購部",
  "amount": 150000
}
```
* **Response (`201 Created`)**:
  回傳建立之訂單明細，並於 HTTP Header 附帶 `Location: /api/v1/orders/{orderNo}`。

```json
{
  "orderId": 1,
  "orderNo": "ORD-20261108-12F6DE26",
  "customerName": "台新銀行採購部",
  "amount": 150000,
  "status": "CREATED",
  "createdAt": "2026-11-08T12:49:45.4862849Z"
}
```

## 實作進度與開發規劃 (Implementation Roadmap)

- [x] **Phase 1: 基礎架構與交易一致性**
  - 完成 .NET 8 專案分層與 MS SQL Schema 設計
  - 實作 `OrderService` 單一交易原子寫入（Orders + OutboxMessages）
- [ ] **Phase 2: 入口防禦與冪等性實作**
  - 實作 `Idempotency-Key` 請求攔截與歷史回應快取
  - 處理併發請求衝突（`409 Conflict`）與異常釋放機制
- [ ] **Phase 3: 非同步事件發布與容錯重試**
  - 建立 `BackgroundService` 定期消費 `PENDING` 狀態事件
  - 實作指數退避（Exponential Backoff + Jitter）重試演算法與死信佇列（DLQ）隔離
- [ ] **Phase 4: 自動對帳與高併發壓測**
  - 實作日終對帳排程（Reconciliation Worker）核對業務與事件狀態
  - 導入 k6 壓測腳本驗證並發去重與寫入吞吐量

## 已知限制與未來優化方向 (Known Limitations)

* **輪詢開銷**：Phase 3 初期採用資料庫 Polling 機制拉取 Outbox，在高併發極限下會對 DB 產生額外讀取壓力；未來可演進為串接 SQL Server CDC（Change Data Capture）或 Debezium 監聽 Transaction Log。
* **歷史資料膨脹**：`OutboxMessages` 隨時間累積會影響索引體積，正式環境需搭配定期歸檔排程（Archive Job）將已完成的冷資料移轉至歷史表。