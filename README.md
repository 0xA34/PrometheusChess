# Prometheus

## DISCLAIMER
First off, not to be confused with [Overwatch®](https://overwatch.blizzard.com) by Blizzard Entertainment, Blizzard named Overwatch as 'pro' or 'prometheus'. I just name like this  because without a name it just nothing.  
By any means, there are a lot of stuff to do, but this is just the concept of how things work in general.  
I might want to improve it? Maybe, but this is a really fun project that I created to understand how modern games nowadays work, hence this project exist in the first place.  
This is also the project that I am attend to Network Programming course, I really appreciate what my professor have taught me. My best regards to you Mr. [REDACTED].  

## Introduction

A server-authoritative online chess game built with .NET 10. The project consists of a TCP game server, a Vulkan-based client, and a shared core library.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Projects](#projects)
- [Communication Protocol](#communication-protocol)
- [Security](#security)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Database Setup](#database-setup)
- [Development Mode](#development-mode)
- [Documentation](#documentation)
- [License](#license)

## Overview

Prometheus is a real-time chess platform where the server maintains authoritative control over all game state. Clients send requests (e.g., move requests), and the server validates and processes them before broadcasting results to relevant players.

Key features:
- Server-authoritative game logic (anti-cheat by design)
- ELO-based matchmaking with expanding rating range
- JWT authentication with session management
- PostgreSQL persistence (with in-memory fallback for development)
- Real-time game clock synchronization
- Vulkan-rendered client with ImGui interface

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         PrometheusVulkan                        │
│                     (Vulkan/ImGui Client)                       │
└─────────────────────────────┬───────────────────────────────────┘
                              │ TCP (JSON over newline-delimited stream)
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                        PrometheusServer                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │   Session   │  │    Game     │  │      Matchmaking        │  │
│  │   Manager   │  │   Manager   │  │        Service          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │   Player    │  │  Security   │  │       Database          │  │
│  │   Manager   │  │   Manager   │  │        Service          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘  │
└─────────────────────────────┬───────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                          ChessCore                              │
│         (Shared: Models, Network Protocol, Security)            │
└─────────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────▼───────────────────────────────────┐
│                         PostgreSQL                              │
│                  (or In-Memory for dev)                         │
└─────────────────────────────────────────────────────────────────┘
```

### Data Flow

1. Client establishes TCP connection to server
2. Client sends `Connect` message, server responds with `ConnectResponse`
3. Client authenticates via `Login` or `Register`
4. Authenticated client can join matchmaking queue
5. When matched, both clients receive `GameStart` message
6. Players send `MoveRequest`, server validates and broadcasts `MoveNotification`
7. Game ends via checkmate, stalemate, resignation, timeout, or draw agreement

## Projects

### ChessCore

Shared library containing:
- **Models**: Game state, pieces, board representation
- **Network**: Message types and serialization (JSON)
- **Security**: JWT token handling, password hashing (BCrypt), rate limiting
- **Logic**: Move validation, check/checkmate detection

Dependencies:
- BCrypt.Net-Next
- Microsoft.IdentityModel.Tokens
- System.IdentityModel.Tokens.Jwt

### PrometheusServer

TCP game server responsible for:
- Connection management (accept, heartbeat, timeout detection)
- Authentication and session management
- Matchmaking (ELO-based with configurable parameters)
- Game state management and move validation
- Database persistence (PostgreSQL via Npgsql)

Dependencies:
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Logging
- Npgsql

### PrometheusVulkan

Desktop client featuring:
- Vulkan-based rendering via Silk.NET
- ImGui user interface
- Network client with automatic reconnection
- Local game state prediction (reconciled with server)

Dependencies:
- Silk.NET (Vulkan, Windowing, Input)
- ImGui.NET
- StbImageSharp

## Communication Protocol

### Transport

- **Protocol**: TCP
- **Port**: 8787 (default)
- **Framing**: Newline-delimited JSON (`\n` terminated)
- **Optimisation**: Nagle's algorithm disabled (`NoDelay = true`)

### Message Types

| Category | Messages |
|----------|----------|
| Connection | `Connect`, `ConnectResponse`, `Heartbeat`, `HeartbeatAck` |
| Authentication | `Login`, `LoginResponse`, `Register`, `RegisterResponse` |
| Matchmaking | `FindMatch`, `CancelFindMatch`, `MatchFound`, `QueueStatus` |
| Game Flow | `GameStart`, `GameState`, `GameEnd` |
| Moves | `MoveRequest`, `MoveResponse`, `MoveNotification` |
| Actions | `Resign`, `OfferDraw`, `DrawOffered`, `AcceptDraw`, `DeclineDraw` |
| Time | `TimeUpdate`, `TimeoutWarning` |
| Error | `Error` |

### Message Structure

All messages inherit from `NetworkMessage`:

```json
{
  "type": 40,
  "timestamp": 1699999999999,
  "messageId": "a1b2c3d4e5f6g7h8"
}
```

### Example: Move Request/Response

Client sends:
```json
{
  "type": 40,
  "sessionToken": "eyJhbGciOiJIUzI1NiIs...",
  "gameId": "abc123",
  "from": "e2",
  "to": "e4",
  "expectedSequence": 1
}
```

Server responds:
```json
{
  "type": 41,
  "success": true,
  "gameId": "abc123",
  "move": "e2e4",
  "newFen": "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
  "isCheck": false,
  "isCheckmate": false,
  "whiteTimeMs": 598000,
  "blackTimeMs": 600000,
  "moveSequence": 1
}
```

Server notifies opponent:
```json
{
  "type": 42,
  "gameId": "abc123",
  "move": "e2e4",
  "newFen": "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
  "isCheck": false,
  "whiteTimeMs": 598000,
  "blackTimeMs": 600000,
  "moveSequence": 1
}
```

## Security

### Authentication

- **Password Storage**: BCrypt with work factor 12
- **Client Transmission**: SHA-256 pre-hash before sending (defense in depth)
- **Session Tokens**: JWT (HS256) with configurable expiration
- **Session Management**: Server-side tracking with revocation support

### Protection Mechanisms

| Mechanism | Purpose |
|-----------|---------|
| Rate Limiting | Prevents brute force and DoS (100 req/min default) |
| Session Limits | Maximum 5 concurrent sessions per player |
| Connection Limits | Maximum 1000 concurrent connections |
| Heartbeat Timeout | Detects dead connections (120s default) |
| Input Validation | Sanitizes all user input |
| Server-Authoritative | All game logic validated server-side |

## Getting Started

### Prerequisites

- .NET 10 SDK
- PostgreSQL 14+ (optional, can use in-memory mode)
- Vulkan-compatible GPU (for client)

### Building

```bash
# Clone the repository
git clone https://github.com/0xA34/Prometheus.git
cd Prometheus

# Build all projects
dotnet build Prometheus.sln

# Or build individual projects
dotnet build PrometheusServer/PrometheusServer.csproj
dotnet build PrometheusVulkan/PrometheusVulkan.csproj
```

### Running the Server

```bash
# Development mode (in-memory, no database required)
dotnet run --project PrometheusServer -- --dev

# Production mode (requires PostgreSQL)
dotnet run --project PrometheusServer
```

### Running the Client

```bash
dotnet run --project PrometheusVulkan
```

## Configuration

Copy the example configuration file and modify as needed:

```bash
cd PrometheusServer
cp appsettings.example.json appsettings.json
```

Edit `appsettings.json` with your settings:

```json
{
  "Server": {
    "Port": 8787,
    "BindAddress": "0.0.0.0",
    "MaxConnections": 1000,
    "HeartbeatIntervalSeconds": 30,
    "ConnectionTimeoutSeconds": 120
  },
  "Security": {
    "JwtSecretKey": "YOUR_SECRET_KEY_AT_LEAST_32_CHARS",
    "TokenExpirationHours": 24,
    "MaxRequestsPerMinute": 100
  },
  "Matchmaking": {
    "DefaultRatingRange": 200,
    "MaxRatingRange": 500,
    "RatingExpansionIntervalSeconds": 30,
    "RatingExpansionAmount": 50
  },
  "Database": {
    "Host": "localhost",
    "Port": 5432,
    "Database": "chess_game",
    "Username": "chess_server",
    "Password": "YOUR_PASSWORD",
    "UseInMemory": false
  },
  "Rating": {
    "DefaultRating": 1200,
    "KFactor": 32,
    "MinRating": 100,
    "MaxRating": 3000
  }
}
```

### Environment Variables

- `CHESS_DB_PASSWORD`: Database password (preferred over config file)
- `ASPNETCORE_ENVIRONMENT`: Set to `Development` for development settings

## Development Mode

Run the server with `--dev` or `--development` flag:

```bash
dotnet run --project PrometheusServer -- --dev
```

Development mode enables:
- In-memory storage (no PostgreSQL required)
- Debug-level logging
- Auto-generated JWT secret (unique per session)
- Relaxed rate limits (1000 req/min)


## Database Setup

The server uses PostgreSQL for persistent storage. A schema script is provided at `database/schema.sql`.

### Quick Setup

```bash
# 1. Create the database
psql -U postgres -c "CREATE DATABASE chess_game;"

# 2. Create a user (optional, can use postgres)
psql -U postgres -c "CREATE USER chess_server WITH PASSWORD 'your_password';"
psql -U postgres -c "GRANT ALL PRIVILEGES ON DATABASE chess_game TO chess_server;"

# 3. Run the schema script
psql -U postgres -d chess_game -f database/schema.sql

# 4. Grant table permissions to the user
psql -U postgres -d chess_game -c "GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO chess_server;"
psql -U postgres -d chess_game -c "GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO chess_server;"
```

### Schema Overview

| Table | Description |
|-------|-------------|
| `players` | User accounts, ratings, game statistics |
| `sessions` | Active login sessions with token hashes |
| `games` | Game records with results and rating changes |
| `game_moves` | Individual move history per game |

### Custom Types

The schema defines PostgreSQL enums for type safety:
- `time_control_type`: bullet, blitz, rapid, classical
- `game_status`: active, completed, aborted
- `game_result`: white, black, draw
- `game_end_reason`: checkmate, stalemate, resignation, timeout, etc.
- `piece_color`: white, black

### Views

- `leaderboard`: Player rankings by rating
- `active_games`: Currently running games
- `player_game_history`: Completed games with results



## License

MIT License - see [LICENSE](LICENSE) for details.
