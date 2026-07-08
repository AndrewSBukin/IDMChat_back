# PROJECT_CONTEXT.md

## 1. Общее описание

**Проект**: Корпоративный чат (10k+ пользователей)  
**Архитектура**: ASP.NET Core 8 + SignalR + EF Core 8 + MSSQL  
**Стиль**: REST API + Real-time (SignalR Hub)  
**Окружение**: Один сервер, Windows IIS (возможен Linux).  
**Текущая версия**: V08+ (стабильная модель данных, soft delete, кэширование).

---

## 2. Технологический стек

| Компонент | Технология | Примечание |
|-----------|------------|------------|
| **Бэкенд** | .NET 8 (C# 12) | Минимальный API не используется |
| **ORM** | EF Core 8 | `QueryTrackingBehavior.NoTracking` (кроме AsTracking) |
| **БД** | MSSQL | Identity на `long` (Message) + UUID (GUID) для сущностей |
| **Real-time** | SignalR | WebSocket, группы, авторизация через `access_token` |
| **Кэш** | `IMemoryCache` + `ConcurrentDictionary` | Для чатов, пользователей, connectionId |
| **Логирование** | NLog / вручную | Файлы с ротацией |
| **Аутентификация** | JWT | Bearer + query `access_token` для SignalR |
| **Файлы** | FFmpeg (thumbnails) + локальное хранилище | BasePath в `appsettings.json` |
| **Тестирование** | xUnit, NBomber (нагрузка) | Отдельные проекты (Пока нет) |

---

## 3. Структура проекта (папки)

- **Controllers**
  - AuthController.cs
  - ConversationsController.cs
  - DevController.cs
  - FilesController.cs
  - HttpContextExtensions.cs
  - ProfileController.cs
  - SettingsController.cs
  - UserController.cs
- **Domain**
  -  Errors.cs
- **DTO**
  - DTOs.cs
- **Hubs**
  - ChatHub.cs
- **Middleware**
  - ActiveUserMiddleware.cs
  - LoggingMiddleware.cs
- **Models**
  - Conversation.cs
  - ConversationMember.cs
  - DbContext.cs
  - FileAttachment.cs
  - Message.cs
  - MessageReadReceipt.cs
  - RefreshToken.cs
  - RequestResponseLog.cs
  - User.cs
- **Services**
  - BackgroundLogQueue.cs
  - LogBatchProcessor.cs
- **Utils**
  - ChatStateCache.cs
  - JsonOptions.cs
  - UserCache.cs
- Program.cs
---

## 4. Ключевые сущности (модели)

### User
- `Id (Guid)`, `DisplayName`, `AvatarUrl`, `Username`, `PasswordHash`, 
- `Status`, `idm`, `Role`, `ConnectionId`, `LastLoginAt`, `IsActive`
- `Phone`, `Email`, `CustomStatus`
- `LastSeenAt` (в БД), `IsOnline` (в кэше)
- Настройки: `NotificationsEnabled`, `SoundEnabled`, `Language`, `Theme`

### Conversation
- `Id (Guid)`, `Type (Direct/Group)`, `Name`, `Idm`, `AvatarUrl`
- `IsWriteRestricted` (только админы), `CreatedAt`, `UpdatedAt`
- Денормализованные поля: `LastMessageId`, `LastMessageText`, `LastMessageSenderId`, `LastMessageCreatedAt`
- Soft delete: `IsDeleted`, `DeletedAt`, `DeletedBy`

### ConversationMember
- `(ConversationId, UserId)` — составной ключ
- `IsAdmin`, `IsPinned`, `IsMuted`, `UnreadCount`, `LastReadMessageId`
- `JoinedAt`, `LastReadMessageId`

### Message
- `Id (long)` — Identity, сортировка по Id
- `ClientTempId (Guid)` — дедупликация
- `SenderId`, `ConversationId`, `Text`, `Type`, `ReplyToMessageId`
- `CreatedAt`, `UpdatedAt`, `SentAt`, `ReplyToMessageId`
- `ChannelId` пока непонятно зачем
- `IsDeleted`, `DeletedAt`, `DeletedBy`

### FileAttachment
- `Id (Guid)`, `MessageId (long?)`, `ConversationId (Guid)`, `UserId`
- `FileName`, `FileSize`, `MimeType`, `StoragePath`, `ThumbnailPath`, `Duration`
- `Type`, `CreatedAt`

### MessageReadReceipt
- `(MessageId, UserId)` — составной ключ, `ReadAt`

---

## 5. Кэширование

### ChatStateCache (IMemoryCache)
- **Ключ**: ConversationId  
- **Данные**: `CachedConversation` (Members, Admins, UnreadCounts, IsWriteRestricted, LastMessage)  
- **Политика**: Sliding 5 мин + Absolute 1 час  
- **Защита**: `SemaphoreSlim` на загрузку из БД (coalescing)

### UserCache (ConcurrentDictionary)
- `_connections` — `UserId → ConnectionId`
- `_displayNames` — `UserId → DisplayName`
- **Методы**: `IsOnline`, `GetOnlineMembers`, `GetConnectionId`

---

## 6. SignalR Hub

### Авторизация
- `[Authorize]` + JWT в query: `?access_token=<token>`
- `Context.GetUserId()` через расширение `HubCallerContextExtensions`

### Основные методы (клиент → сервер)

| Метод | Описание |
|-------|----------|
| `SendMessage(...)` | Отправка текста/медиа/voice, reply, attachments |
| `JoinConversation(conversationId)` | Вход в группу чата, отправка непрочитанных |
| `LeaveConversation(conversationId)` | Выход из группы |
| `SendTyping(conversationId)` | Событие начала печати |
| `StopTyping(conversationId)` | Событие остановки печати |

### События (сервер → клиент)

| Событие | Назначение |
|---------|------------|
| `message_new` | Новое сообщение (полный объект) → группа |
| `message_confirmed` | Подтверждение автору |
| `message_delivered` | Список доставленных получателей |
| `message_read` | Прочтение сообщения |
| `message_edited` | Редактирование |
| `message_deleted` | Удаление |
| `conversation_updated` | Обновление чата (last_message, name, avatar) |
| `conversation_new` | Полный объект чата для нового участника |
| `members_added` | Список добавленных участников |
| `unread_count_updated` | Обновление счётчика |
| `user_status` | Статус пользователя (online/offline) |
| `unread_summary` | Сводка по непрочитанным при подключении |

---

## 7. REST API (основные эндпоинты)

| Метод | URL | Описание |
|-------|-----|----------|
| GET | `/api/v1/conversations` | Список чатов (limit/offset) |
| POST | `/api/v1/conversations` | Создать чат |
| PATCH | `/api/v1/conversations/{id}` | Обновить название/аватар |
| DELETE | `/api/v1/conversations/{id}` | Покинуть/удалить |
| PATCH | `/api/v1/conversations/{id}/pin` | Закрепить |
| PATCH | `/api/v1/conversations/{id}/mute` | Выключить уведомления |
| POST | `/api/v1/conversations/{id}/members` | Добавить участников |
| DELETE | `/api/v1/conversations/{id}/members/{userId}` | Удалить участника |
| GET | `/api/v1/conversations/{id}/messages` | История (limit, before, after) |
| PATCH | `/api/v1/conversations/{id}/messages/{messageId}` | Редактировать |
| DELETE | `/api/v1/conversations/{id}/messages/{messageId}` | Удалить |
| POST | `/api/v1/conversations/{id}/read` | Отметить прочитанным |
| POST | `/api/v1/files/upload` | Загрузить файл |
| GET | `/api/v1/files/{*path}` | Получить файл |
| POST | `/api/v1/profile/avatar` | Загрузить аватар пользователя |
| POST | `/api/v1/conversations/{id}/avatar` | Загрузить аватар группы |

---

## 8. Soft Delete

**Поля**:
- `IsDeleted` (bool)
- `DeletedAt` (DateTime?)
- `DeletedBy` (Guid?)

**Где используется**:
- `Conversation` (чаты не удаляются физически)
- `Message` (сообщения помечаются удалёнными)

**`ConversationMember`** — удаляется физически (без soft delete).

---

## 9. Файлы и медиа

**Хранилище**:
- Путь: `uploads/` в корне приложения (конфигурируется через `Storage:BasePath`)
- Структура: `files/{yyyy/MM/dd}/{uuid}.ext`, `avatars/users/{subfolder}/...`, `avatars/conversations/...`

**Thumbnails**:
- Для изображений: через FFmpeg
- Для видео: FFmpeg + FFprobe (длительность, кадр)

**Загрузка**:
- Отдельный эндпоинт `POST /api/v1/files/upload`
- Привязка к сообщению через `FileAttachment.MessageId`
- Абсолютные ссылки формируются ChatPathUrlResolver.ResolveUrl(relativeUrl)

---

## 10. Сборка, миграции, окружение

**EF Core особенности**:
- `NoTracking` по умолчанию → при обновлениях использовать `.AsTracking()` или `ExecuteUpdateAsync`
- Для составных ключей — `[PrimaryKey]` или `HasKey` в `OnModelCreating`
- Для SQL Server: `UseSqlServer`, `MigrationsAssembly`

**AppSettings**:
- `Storage:BasePath` — корень для файлов (по умолчанию `uploads/`)

---

## 11. Важные устоявшиеся решения

- **Id сообщений** — `long` (для сортировки), а `ClientTempId` (Guid) — для дедупликации
- **Авторизация в хабе** — через `access_token` в query-строке
- **Unread_count** — хранится в `ConversationMember`, пересчитывается при `MarkAsRead`, обновляется в кэше
- **Навигации** — `Message.Sender`, `Message.ReplyToMessage` — используются с `Include`, чтобы избежать N+1
- **Денормализация** — `LastMessage*` в `Conversation` для быстрого списка чатов (не работает, приходится читать из базы)
- **Reply_to** — загружается одним дополнительным запросом в `GetMessages`
- **Attachments** — загружаются отдельным запросом в `GetMessages` и еще в куче мест!
- **ChatStateCache** — Singleton, загружает чат из БД при первом запросе, защищён от cache stampede
- **UserCache** — Singleton, хранит ConnectionId и DisplayName в памяти

---

## 12. Актуальные задачи
- ForwardMessages
- Реакции на сообщения
- Закрепление сообщений
- Списки медиа в чате (проверить, что работают), добавить ссылки. 
(по поводу ссылок важно, что не надо их искать каждый раз, иначе придется читать и анализировать все сообщения чата)
- Настройка прав в чате (выдавать права админа, может еще что)
- Push уведомления

---

## 13. TODO проекта

1. (Исправлено) По мере исправления ошибок количество чтений из базы выросло в разы почти во всех методах. Скорее всего заявленной скорости нет.
2. (Исправлено) Кеш такое ощущение, что в кеше не хватает данных или про него забыли, так как приходится почти все читать из базы. Например, есть подозрение на GetUserDisplayNameAndAvatar, которая читает из базы (используется в 7 местах)
3. Когда надо обновлять LastSeenAt пользователя? Сейчас только на логине / рефреше.
4. Привести в порядок названия DTO
5. Нет тестов. Внутри этого же решения проект с тестами ломает автопубликацию через гитхаб.
6. Возможно стоит переделать ручное логирование на NLog. Как в NLog создать разные логи?
7. Файлы контроллеров разрослись.
8. По коду есть похожие куски кода, но с небольшими отличиями. Быстро не удалось объединить их в переиспользуемые методы, а хочется.
9. В FileAttachment есть поле ConversationId, но оно мало где заполнено и вообще непонятно зачем оно.
10. По возможности разобраться с миграциями. При изменениях в структуре не получается сделать миграцию. Приходится удалять все миграции пересоздавать миграцию заново, предварительно внеся изменения в БД вручную.

*Актуально на 06.07.2026*