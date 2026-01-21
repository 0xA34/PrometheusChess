-- Prometheus Chess Server Database Schema
-- PostgreSQL 14+
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE chess_game;"
--   psql -U postgres -d chess_game -f schema.sql
--
-- Or with a specific user:
--   psql -U chess_server -d chess_game -f schema.sql

-- ============================================================================
-- EXTENSIONS
-- ============================================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "citext";

-- ============================================================================
-- CUSTOM TYPES
-- ============================================================================

-- Time control categories
DO $$ BEGIN
    CREATE TYPE time_control_type AS ENUM ('bullet', 'blitz', 'rapid', 'classical');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- Game status
DO $$ BEGIN
    CREATE TYPE game_status AS ENUM ('active', 'completed', 'aborted');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- Game result
DO $$ BEGIN
    CREATE TYPE game_result AS ENUM ('white', 'black', 'draw');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- Game end reason
DO $$ BEGIN
    CREATE TYPE game_end_reason AS ENUM (
        'checkmate',
        'stalemate',
        'resignation',
        'timeout',
        'draw_agreement',
        'insufficient_material',
        'fifty_move_rule',
        'threefold_repetition',
        'disconnection',
        'aborted'
    );
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- Piece color
DO $$ BEGIN
    CREATE TYPE piece_color AS ENUM ('white', 'black');
EXCEPTION
    WHEN duplicate_object THEN NULL;
END $$;

-- ============================================================================
-- TABLES
-- ============================================================================

-- Players table
CREATE TABLE IF NOT EXISTS players (
    player_id       UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    username        VARCHAR(32) NOT NULL,
    email           CITEXT NOT NULL,
    password_hash   VARCHAR(255) NOT NULL,
    rating          INTEGER NOT NULL DEFAULT 1200,
    games_played    INTEGER NOT NULL DEFAULT 0,
    games_won       INTEGER NOT NULL DEFAULT 0,
    games_lost      INTEGER NOT NULL DEFAULT 0,
    games_drawn     INTEGER NOT NULL DEFAULT 0,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    last_login_at   TIMESTAMP WITH TIME ZONE,
    is_banned       BOOLEAN NOT NULL DEFAULT FALSE,
    ban_reason      TEXT,

    CONSTRAINT username_length CHECK (LENGTH(username) >= 3 AND LENGTH(username) <= 32),
    CONSTRAINT username_format CHECK (username ~ '^[a-zA-Z0-9_]+$'),
    CONSTRAINT email_format CHECK (email ~ '^[^@]+@[^@]+\.[^@]+$'),
    CONSTRAINT rating_range CHECK (rating >= 100 AND rating <= 3500),
    CONSTRAINT games_non_negative CHECK (
        games_played >= 0 AND
        games_won >= 0 AND
        games_lost >= 0 AND
        games_drawn >= 0
    ),
    CONSTRAINT games_sum CHECK (games_played = games_won + games_lost + games_drawn)
);

-- Unique constraints for players
CREATE UNIQUE INDEX IF NOT EXISTS idx_players_username_lower ON players (LOWER(username));
CREATE UNIQUE INDEX IF NOT EXISTS idx_players_email ON players (email);

-- Sessions table
CREATE TABLE IF NOT EXISTS sessions (
    session_id      UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    player_id       UUID NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    token_hash      VARCHAR(64) NOT NULL,
    created_at      TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    expires_at      TIMESTAMP WITH TIME ZONE NOT NULL,
    last_activity   TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    ip_address      INET,
    user_agent      TEXT,
    is_revoked      BOOLEAN NOT NULL DEFAULT FALSE,
    revoked_reason  TEXT,

    CONSTRAINT expires_after_created CHECK (expires_at > created_at)
);

-- Indexes for sessions
CREATE INDEX IF NOT EXISTS idx_sessions_player_id ON sessions(player_id);
CREATE INDEX IF NOT EXISTS idx_sessions_token_hash ON sessions(token_hash);
CREATE INDEX IF NOT EXISTS idx_sessions_expires_at ON sessions(expires_at);
CREATE INDEX IF NOT EXISTS idx_sessions_active ON sessions(player_id, is_revoked, expires_at)
    WHERE is_revoked = FALSE;

-- Games table
CREATE TABLE IF NOT EXISTS games (
    game_id             UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    white_player_id     UUID NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    black_player_id     UUID NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    status              game_status NOT NULL DEFAULT 'active',
    result              game_result,
    end_reason          game_end_reason,
    time_control        time_control_type NOT NULL,
    initial_time_ms     INTEGER NOT NULL,
    increment_ms        INTEGER NOT NULL DEFAULT 0,
    pgn                 TEXT,
    final_fen           VARCHAR(100),
    started_at          TIMESTAMP WITH TIME ZONE,
    ended_at            TIMESTAMP WITH TIME ZONE,
    white_rating_before INTEGER,
    black_rating_before INTEGER,
    white_rating_change INTEGER,
    black_rating_change INTEGER,
    created_at          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),

    CONSTRAINT different_players CHECK (white_player_id != black_player_id),
    CONSTRAINT initial_time_positive CHECK (initial_time_ms > 0),
    CONSTRAINT increment_non_negative CHECK (increment_ms >= 0),
    CONSTRAINT completed_has_result CHECK (
        (status = 'completed' AND result IS NOT NULL AND end_reason IS NOT NULL) OR
        (status != 'completed')
    )
);

-- Indexes for games
CREATE INDEX IF NOT EXISTS idx_games_white_player ON games(white_player_id);
CREATE INDEX IF NOT EXISTS idx_games_black_player ON games(black_player_id);
CREATE INDEX IF NOT EXISTS idx_games_status ON games(status);
CREATE INDEX IF NOT EXISTS idx_games_created_at ON games(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_games_player_history ON games(white_player_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_games_active ON games(status) WHERE status = 'active';

-- Game moves table
CREATE TABLE IF NOT EXISTS game_moves (
    move_id             SERIAL PRIMARY KEY,
    game_id             UUID NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
    move_number         INTEGER NOT NULL,
    color               piece_color NOT NULL,
    from_square         CHAR(2) NOT NULL,
    to_square           CHAR(2) NOT NULL,
    promotion           CHAR(1),
    san_notation        VARCHAR(10),
    fen_after           VARCHAR(100) NOT NULL,
    time_remaining_ms   INTEGER,
    move_time_ms        INTEGER,
    created_at          TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),

    CONSTRAINT move_number_positive CHECK (move_number > 0),
    CONSTRAINT valid_square_from CHECK (from_square ~ '^[a-h][1-8]$'),
    CONSTRAINT valid_square_to CHECK (to_square ~ '^[a-h][1-8]$'),
    CONSTRAINT valid_promotion CHECK (promotion IS NULL OR promotion IN ('q', 'r', 'b', 'n')),
    CONSTRAINT time_non_negative CHECK (
        (time_remaining_ms IS NULL OR time_remaining_ms >= 0) AND
        (move_time_ms IS NULL OR move_time_ms >= 0)
    )
);

-- Indexes for game_moves
CREATE INDEX IF NOT EXISTS idx_game_moves_game_id ON game_moves(game_id);
CREATE INDEX IF NOT EXISTS idx_game_moves_order ON game_moves(game_id, move_number, color);
CREATE UNIQUE INDEX IF NOT EXISTS idx_game_moves_unique ON game_moves(game_id, move_number, color);

-- ============================================================================
-- FUNCTIONS
-- ============================================================================

-- Function to update last_activity on session access
CREATE OR REPLACE FUNCTION update_session_activity()
RETURNS TRIGGER AS $$
BEGIN
    NEW.last_activity = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Function to update player stats after game completion
CREATE OR REPLACE FUNCTION update_player_stats_on_game_complete()
RETURNS TRIGGER AS $$
BEGIN
    -- Only process when game is completed
    IF NEW.status = 'completed' AND OLD.status = 'active' THEN
        -- Update white player stats
        UPDATE players SET
            games_played = games_played + 1,
            games_won = games_won + CASE WHEN NEW.result = 'white' THEN 1 ELSE 0 END,
            games_lost = games_lost + CASE WHEN NEW.result = 'black' THEN 1 ELSE 0 END,
            games_drawn = games_drawn + CASE WHEN NEW.result = 'draw' THEN 1 ELSE 0 END,
            rating = rating + COALESCE(NEW.white_rating_change, 0)
        WHERE player_id = NEW.white_player_id;

        -- Update black player stats
        UPDATE players SET
            games_played = games_played + 1,
            games_won = games_won + CASE WHEN NEW.result = 'black' THEN 1 ELSE 0 END,
            games_lost = games_lost + CASE WHEN NEW.result = 'white' THEN 1 ELSE 0 END,
            games_drawn = games_drawn + CASE WHEN NEW.result = 'draw' THEN 1 ELSE 0 END,
            rating = rating + COALESCE(NEW.black_rating_change, 0)
        WHERE player_id = NEW.black_player_id;
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Function to cleanup expired sessions
CREATE OR REPLACE FUNCTION cleanup_expired_sessions()
RETURNS INTEGER AS $$
DECLARE
    deleted_count INTEGER;
BEGIN
    DELETE FROM sessions
    WHERE expires_at < NOW() OR is_revoked = TRUE;

    GET DIAGNOSTICS deleted_count = ROW_COUNT;
    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- TRIGGERS
-- ============================================================================

-- Trigger for game completion stats update
DROP TRIGGER IF EXISTS trg_game_complete_stats ON games;
CREATE TRIGGER trg_game_complete_stats
    AFTER UPDATE ON games
    FOR EACH ROW
    EXECUTE FUNCTION update_player_stats_on_game_complete();

-- ============================================================================
-- VIEWS
-- ============================================================================

-- Leaderboard view
CREATE OR REPLACE VIEW leaderboard AS
SELECT
    player_id,
    username,
    rating,
    games_played,
    games_won,
    games_lost,
    games_drawn,
    CASE
        WHEN games_played > 0 THEN ROUND(100.0 * games_won / games_played, 1)
        ELSE 0
    END AS win_percentage,
    RANK() OVER (ORDER BY rating DESC) AS rank
FROM players
WHERE is_banned = FALSE AND games_played > 0
ORDER BY rating DESC;

-- Active games view
CREATE OR REPLACE VIEW active_games AS
SELECT
    g.game_id,
    g.white_player_id,
    wp.username AS white_username,
    g.black_player_id,
    bp.username AS black_username,
    g.time_control,
    g.initial_time_ms,
    g.increment_ms,
    g.started_at,
    g.created_at
FROM games g
JOIN players wp ON g.white_player_id = wp.player_id
JOIN players bp ON g.black_player_id = bp.player_id
WHERE g.status = 'active';

-- Player game history view
CREATE OR REPLACE VIEW player_game_history AS
SELECT
    g.game_id,
    g.white_player_id,
    wp.username AS white_username,
    g.black_player_id,
    bp.username AS black_username,
    g.status,
    g.result,
    g.end_reason,
    g.time_control,
    g.started_at,
    g.ended_at,
    g.white_rating_before,
    g.black_rating_before,
    g.white_rating_change,
    g.black_rating_change
FROM games g
JOIN players wp ON g.white_player_id = wp.player_id
JOIN players bp ON g.black_player_id = bp.player_id
WHERE g.status = 'completed'
ORDER BY g.ended_at DESC;

-- ============================================================================
-- DEFAULT DATA (Optional)
-- ============================================================================

-- You can uncomment the following to create a test user
-- Password hash is for 'password123' using BCrypt with work factor 12
-- INSERT INTO players (username, email, password_hash, rating)
-- VALUES ('testuser', 'test@example.com', '$2a$12$LQv3c1yqBWVHxkd0LHAkCOYz6TtxMQJqhN8/X4.VTtYA.qxQvq2uy', 1200)
-- ON CONFLICT DO NOTHING;

-- ============================================================================
-- GRANTS (adjust as needed for your setup)
-- ============================================================================

-- Grant permissions to chess_server user (uncomment and modify as needed)
-- GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO chess_server;
-- GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO chess_server;
-- GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO chess_server;

-- ============================================================================
-- MAINTENANCE
-- ============================================================================

-- Analyze tables for query optimization
ANALYZE players;
ANALYZE sessions;
ANALYZE games;
ANALYZE game_moves;

-- Display summary
DO $$
BEGIN
    RAISE NOTICE '================================================';
    RAISE NOTICE 'Prometheus database schema created successfully!';
    RAISE NOTICE '================================================';
    RAISE NOTICE 'Tables created: players, sessions, games, game_moves';
    RAISE NOTICE 'Views created: leaderboard, active_games, player_game_history';
    RAISE NOTICE '';
    RAISE NOTICE 'Next steps:';
    RAISE NOTICE '1. Create a database user if not exists:';
    RAISE NOTICE '   CREATE USER chess_server WITH PASSWORD ''your_password'';';
    RAISE NOTICE '2. Grant permissions:';
    RAISE NOTICE '   GRANT ALL PRIVILEGES ON DATABASE chess_game TO chess_server;';
    RAISE NOTICE '   GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO chess_server;';
    RAISE NOTICE '   GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO chess_server;';
    RAISE NOTICE '================================================';
END $$;
