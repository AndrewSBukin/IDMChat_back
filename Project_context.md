# 0. Системные настройки контекста
[Crytical] [SYSTEM]
Ты - специалист по разработке web приложений на .NET.
Ты предлагаешь только best practices решения и подходы.
Ты всегда предлагаешь только продуманные, оптимальные по производительности решения.
Ты стараешься сделать код простым.
При поступлении новой задачи не надо сразу писать код. Надо сначала обсудить возможные подходы, их плюсы и минусы.
При решении задачи не надо сразу писать инструкцию из многих пунктов. Пиши только пару первых шагов и сколько их будет всего.


# PROJECT_CONTEXT.md

## 1. Общее описание

**Проект**: Корпоративный чат (10k+ пользователей)  
**Архитектура**: ASP.NET Core 8 + SignalR + EF Core 8 + MSSQL  
**Стиль**: REST API + Real-time (SignalR Hub)  
**Окружение**: Один сервер, Windows IIS (возможен Linux).  
**Текущая версия**: V09.

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
| **Push** | Firebase Cloud Messaging (FCM) | |

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

## 4. Схема базы данных (Entities & Fluent API)
В системе реализован гибридный RBAC/ACL подход управления доступом на основе справочников, ролей-шаблонов и индивидуальных пер-юзер оверрайдов (принцип Fail-Closed).


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

### Настройка доступов
```
public enum AccessEffect { Grant = 1, Deny = 2 }

// Справочник разделов меню
public class Section
{
    public string Key { get; set; } = null!; // PK, напр. "office.staff", "app.chat"
    public string Scope { get; set; } = null!; // "app" или "club"
    public string Title { get; set; } = null!;
    public string Icon { get; set; } = null!; // Семантический алиас иконки ("home")
    public int Order { get; set; }
    public string? ParentKey { get; set; }
    public Section? Parent { get; set; }
    public ICollection<Section> Children { get; set; } = new List<Section>();
    public bool IsActive { get; set; } = true;
}

// Справочник атомарных прав
public class Permission
{
    public string Key { get; set; } = null!; // PK, напр. "daily.expense.edit"
    public string Description { get; set; } = null!;
}

// Таблица системных ролей
public class Role
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!; // "manager", "cashier"
    public string Name { get; set; } = null!;
}

// Таблицы связей шаблонов ролей
public class RoleSection { public Guid RoleId { get; set; }; public string SectionKey { get; set; } = null!; }
public class RolePermission { public Guid RoleId { get; set; }; public string PermissionKey { get; set; } = null!; }

// Профиль пользователя и настройки приземления
public class UserProfile
{
    public Guid UserId { get; set; } // PK / FK на таблицу Users
    public Guid? RoleId { get; set; }
    public Role? Role { get; set; }
    public string? DefaultSectionKey { get; set; } // FK на Section
    public string? ClubLandingSectionKey { get; set; } // FK на Section
}

// Пер-юзер оверрайды доступа
public class UserSectionOverride { public Guid UserId { get; set; }; public string SectionKey { get; set; } = null!; public AccessEffect Effect { get; set; } }
public class UserPermissionOverride { public Guid UserId { get; set; }; public string PermissionKey { get; set; } = null!; public AccessEffect Effect { get; set; } }
public class UserLimit { public Guid UserId { get; set; }; public string LimitKey { get; set; } = null!; public int IntValue { get; set; } }
public class UserClub { public Guid UserId { get; set; }; public int ClubId { get; set; } }

// Таблица клубов с денормализованным городом (Оптимизация под 10k+)
public class Club
{
    public int Id { get; set; } // PK
    public string Name { get; set; } = null!; // "АКС1828"
    public string Code { get; set; } = null!; // "1828" (маппится в bbID контракта)
    public string Company { get; set; } = null!; // idm / код компании
    public string CityName { get; set; } = null!; // "Ростов-на-Дону"
    public int CityGmt { get; set; } // Смещение таймзоны (напр. 3)
}
```

### Конфигурация Fluent API (`OnModelCreating`):
* Настроена self-referencing связь `Section.Parent` с `DeleteBehavior.Restrict`.
* Объявлены составные первичные ключи для всех связующих таблиц и оверрайдов.
* Созданы **неуникальные индексы** (`IX_*_UserId`) для `UserSectionOverride`, `UserPermissionOverride`, `UserLimit`, `UserClub` для мгновенной сборки контекста пользователя.

### Итоговый контракт авторизации (`POST /api/auth/login`)
Для сохранения обратной совместимости с вебом применена аддитивная стратегия миграции.

```json
{
  "access_token": "ey...",
  "refresh_token": "...",
  "expires_in": 3600,
  "user": {
    "id": "7b0d0450-...",
    "username": "admin",
    "display_name": "Иван Петров",
    "avatar_url": "https://idmbb.ru",
    "avatar_thumb_url": "https://idmbb.ru",
    "is_online": true,
    "last_seen_at": "2026-08-13T00:00:00Z",
    "role": "admin",
    "fullName": "Иван Петров",
    "defaultSection": { "key": "club.general", "scope": "club" },
    "clubLandingKey": "club.daily"
  },
  "permissions": [
    "daily.expense.edit", "daily.view.history", "daily.view.today", "monthly.full"
  ],
  "limits": { "daily.history.days": 30 },
  "clubs": [
    { "id": 225, "bbID": "1828", "name": "АКС1828", "city": { "name": "Ростов-на-Дону", "gmt": 3 } }
  ],
  "menu": [
    { "key": "app.chat", "scope": "app", "title": "Список чатов", "icon": "chat", "order": 0, "children": [] },
    { "key": "club.hall.management", "scope": "club", "title": "Управление клубом", "icon": "management", "order": 2,
      "children": [
        { "key": "club.hall.staff", "title": "Сотрудники клуба", "icon": "staff", "order": 1 }
      ]
    }
  ]
}
```
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

### 5.1 Кэширующий сервис `AuthContextService`
* Инкапсулирует вычисление эффективных прав, слияние шаблонов ролей и индивидуальных `Grant/Deny` оверрайдов.
* Строит иерархическое меню строго в 1 уровень вложенности, дочерние пункты наследуют `scope` родителя.
* Использует внутрипроцессный `IMemoryCache` со стратегией защиты RAM: `AbsoluteExpirationRelativeToNow = 24 часа`, `SlidingExpiration = 30 минут`. Сборка из БД происходит только при промахе кэша за один проход. Имя пользователя запрашивается из глобального `_userCache` без дергания СУБД.
* Содержит метод `InvalidateCache(Guid userId)` для сброса данных при изменении прав в админке.

### 5.2 Семантический шлюз ИДМ (`MenuGatewayController`) **ЕЩЕ НЕ РЕАЛИЗОВАНО!**
* Принимает запросы с семантическим ключом экрана (напр. `GET /api/v1/menugateway/club/225/club.daily`).
* Осуществляет двойной рубеж защиты: проверяет наличие ключа в меню пользователя и валидирует, привязан ли `clubId` к его профилю.
* Навязывает лимиты на уровне бэкенда: при отсутствии у пользователя права `daily.view.history` шлюз жестко зажимает параметры внешнего HTTP-запроса к ИДМ текущей датой (`mode=today-only`), предотвращая взлом со стороны клиента.

### 5.3 Универсальный `UrlResolver` путей файлов
* Обладает свойством идемпотентности: если строка уже содержит `http://` или `https://`, она возвращается без изменений.
* Полностью автономен (не требует `_basePath`): с помощью маркеров `storageFolders = new[] { "avatars/", "attachments/", "thumbnails/", "files/" }` он автоматически определяет относительный путь, отсекая абсолютную дисковую структуру серверов разработки Windows/Linux.

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
| `message_delivered` | Сообщение доставлено онлайн пользователю |
| `message_duplicate` | Если сообщение уже отправлено |
| `conversation_updated` | Обновление чата (last_message, name, avatar) |
| `conversation_options_updated` | Обновление личных настроек чата (is_pinned, is_muted) |
| `conversation_new` | Полный объект чата для нового участника |
| `members_added` | Список добавленных участников |
| `members_removed` | Список добавленных участников |
| `typing_start` | Событие начала печати |
| `typing_stop` | Событие остановки печати |
| `unread_count_updated` | Обновление счётчика |
| `user_status` | Статус пользователя (online/offline) |
| `unread_summary` | Сводка по непрочитанным при подключении |

### 6.1 Персональная сборка DTO (`BuildConversationUpdatedDto`)
* Модифицирован для приема `Guid userId`. Все счётчики (`unread_count`) и массивы упоминаний (`unread_mention_ids`) рассчитываются **персонально** для каждого получателя.
* Массив непрочитанных упоминаний `unread_mention_ids` собирается динамически из таблицы `MessageMentions` по условию `m.Id > cm.LastReadMessageId && !m.IsDeleted` в порядке от старых к новым (`OrderBy(m => m.Id)`).
* Для `direct`-чатов метод автоматически подставляет в качестве `name`, `avatar_url` и `avatar_thumb_url` данные собеседника.
* Все вызовы событий `conversation_new` и `conversation_updated` в контроллерах переведены на персональную итерацию в цикле `foreach` по участникам чата и отправку через адресный метод `Clients.Client(connectionId)` в фоновом режиме (fire-and-forget).

### 6.2 Обратная совместимость ивент-модели
* Чтобы не ломать логику фронтенда, ивент `unread_count_updated` сохранил свой прежний формат (без массивов строк).
* Для управления кнопкой `@` внедрено **новое изолированное SignalR-событие `unread_mentions_updated`**, передающее payload вида `{ conversation_id, unread_mention_ids: ["3660", "3712"] }`. Оно триггерится при получении сообщения, удалении сообщения и вызове метода `MarkAsRead`.

### 6.3 Оптимизация удаления участников чата (`RemoveMember`)
* Исправлена уязвимость гонки условий (Race Condition): перед физическим удалением записи `ConversationMember` из базы данных, бэкенд извлекает `connectionId` удаляемого пользователя из `_userCache`. 
* Событие `members_removed` отправляется как в общую сокет-группу чата, так и **адресно на сокет исключенного человека**, что гарантирует мгновенный сброс его UI, даже если SignalR успел выбросить его из группы на основе обновлений СУБД.

### 6.4 Пакетный обработчик `FlushBatchAsync`
* Разделен контур реалтайма и push-уведомлений. Сначала воркер собирает из батча все упоминания и пачечным SQL-запросом вычисляет актуальные `UnreadCount` и `UnreadMentionIds` для онлайн-пользователей, рассылая им SignalR уведомления. Затем переходит к формированию мобильных пушей Firebase (FCM).
* Внедрена высоконагруженная оптимизация чистки устаревших сессий: при получении от Firebase ошибок `Unregistered` или `InvalidArgument`, строки токенов собираются в `List<string> deadTokenStrings`. По окончании обработки чанка база данных чистится **одним пачечным вызовом `db.DeviceTokens.RemoveRange`**, что спасает MSSQL от перегрузок СУБД.

### 6.5 Инициализация при вступлении в чат
* В метод добавления участников интегрировано автоматическое прочтение истории: при создании записи `ConversationMember` поле `LastReadMessageId` заполняется актуальным денормализованным значением `LastMessageId` из сущности беседы. Новый пользователь заходит в чат с `UnreadCount = 0` и чистой кнопкой `@`, без ложных уведомлений за прошлые периоды.

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
- Отдельный эндпоинт `POST /api/v1/files/upload` aka `POST /api/files/upload` без авторизации
- Привязка к сообщению через `FileAttachment.MessageId`
- Абсолютные ссылки формируются ChatPathUrlResolver.ResolveUrl(relativeUrl)

---

## 10. Сборка, миграции, окружение

**EF Core особенности**:
- `NoTracking` по умолчанию → при обновлениях использовать `.AsTracking()` или `ExecuteUpdateAsync`
- Для составных ключей — `[PrimaryKey]`
- Для SQL Server: `UseSqlServer`, `MigrationsAssembly` командой `dotnet ef migrations bundle -r win-x64 --force`

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
- **UserCache** — Singleton, хранит ConnectionId тех, кто онлайн и DisplayName, AvatarUrl, CustomStatus и LastSeenAt всех в памяти

---

## 12. Актуальные задачи
- Забирать актуальный список клубов пользователя из ИДМ каждый раз или периодически
- Интегрировать доступ с системой ИДМ (однократно по ролям пользователей)
- Подтянуть меню пользователей из ИДМ (однократно) 
- запросы отчетов передавать в ИДМ и отдавать ответ клиенту чата
- Реализовать команды
- Добавить клавиатуру в сообщении

---

## 13. TODO проекта

- Проверить, что код работает для двух устройств под одним логином.
- Нет тестов. Внутри этого же решения проект с тестами ломает автопубликацию через гитхаб.
- Обновлять кеш при любом изменении (есть подозрение, что не везде обновляется)
- Рассмотреть выгоду от создания кеша members
- По коду есть похожие куски кода, но с небольшими отличиями. В частности использовать MessageDtoMapper.
- Файлы контроллеров разрослись.
- Привести в порядок названия DTO
- Проблема неэффективной обработки медиа (вынос синхронной генерации превью видео/файлов через FFmpeg из веб-потоков контроллера в фоновый BackgroundService)

*Актуально на 12.08.2026*