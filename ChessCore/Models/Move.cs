namespace ChessCore.Models;

/// <summary>
/// Represents a chess move from one position to another
/// </summary>
public sealed record Move
{
    /// <summary>
    /// The starting position of the piece
    /// </summary>
    public Position From { get; init; }

    /// <summary>
    /// The destination position of the piece
    /// </summary>
    public Position To { get; init; }

    /// <summary>
    /// The type of piece being moved (for validation)
    /// </summary>
    public PieceType PieceType { get; init; }

    /// <summary>
    /// The color of the player making the move
    /// </summary>
    public PieceColor PlayerColor { get; init; }

    /// <summary>
    /// The type to promote to if this is a pawn promotion move (null otherwise)
    /// </summary>
    public PieceType? PromotionType { get; init; }

    /// <summary>
    /// Special move flags (capture, castling, en passant, etc.)
    /// </summary>
    public SpecialMoveType SpecialMoveFlags { get; init; } = SpecialMoveType.None;

    /// <summary>
    /// Server timestamp when the move was validated (set by server only)
    /// </summary>
    public long ServerTimestamp { get; init; }

    /// <summary>
    /// Client timestamp when the move was requested (for latency tracking)
    /// </summary>
    public long ClientTimestamp { get; init; }

    /// <summary>
    /// The piece that was captured, if any
    /// </summary>
    public PieceType? CapturedPiece { get; init; }

    /// <summary>
    /// Unique identifier for this move (useful for network synchronization)
    /// </summary>
    public Guid MoveId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Creates a new move
    /// </summary>
    public Move(Position from, Position to, PieceType pieceType, PieceColor playerColor)
    {
        From = from;
        To = to;
        PieceType = pieceType;
        PlayerColor = playerColor;
    }

    /// <summary>
    /// Parameterless constructor for serialization
    /// </summary>
    public Move()
    {
    }

    /// <summary>
    /// Creates a move from algebraic notation (e.g., "e2e4", "e7e8q" for promotion)
    /// </summary>
    /// <param name="notation">Move in coordinate notation (e.g., "e2e4")</param>
    /// <param name="pieceType">The type of piece being moved</param>
    /// <param name="playerColor">The color of the player making the move</param>
    /// <returns>A new Move object</returns>
    public static Move FromCoordinateNotation(string notation, PieceType pieceType, PieceColor playerColor)
    {
        if (string.IsNullOrEmpty(notation) || notation.Length < 4 || notation.Length > 5)
            throw new ArgumentException("Invalid coordinate notation", nameof(notation));

        var from = Position.FromAlgebraic(notation[..2]);
        var to = Position.FromAlgebraic(notation[2..4]);

        PieceType? promotionType = null;
        if (notation.Length == 5)
        {
            promotionType = char.ToLower(notation[4]) switch
            {
                'q' => PieceType.Queen,
                'r' => PieceType.Rook,
                'b' => PieceType.Bishop,
                'n' => PieceType.Knight,
                _ => throw new ArgumentException($"Invalid promotion piece: {notation[4]}", nameof(notation))
            };
        }

        return new Move(from, to, pieceType, playerColor)
        {
            PromotionType = promotionType,
            SpecialMoveFlags = promotionType.HasValue ? SpecialMoveType.PawnPromotion : SpecialMoveType.None
        };
    }

    /// <summary>
    /// Returns the move in coordinate notation (e.g., "e2e4", "e7e8q")
    /// </summary>
    public string ToCoordinateNotation()
    {
        string notation = $"{From.ToAlgebraic()}{To.ToAlgebraic()}";

        if (PromotionType.HasValue)
        {
            char promotionChar = PromotionType.Value switch
            {
                PieceType.Queen => 'q',
                PieceType.Rook => 'r',
                PieceType.Bishop => 'b',
                PieceType.Knight => 'n',
                _ => throw new InvalidOperationException("Invalid promotion type")
            };
            notation += promotionChar;
        }

        return notation;
    }

    /// <summary>
    /// Returns the move in Standard Algebraic Notation (SAN)
    /// Note: This is a simplified version; full SAN requires board context for disambiguation
    /// </summary>
    public string ToSanNotation()
    {
        // Handle castling
        if (SpecialMoveFlags.HasFlag(SpecialMoveType.CastleKingside))
            return "O-O";
        if (SpecialMoveFlags.HasFlag(SpecialMoveType.CastleQueenside))
            return "O-O-O";

        string notation = "";

        // Piece symbol (pawns have no symbol)
        if (PieceType != PieceType.Pawn)
        {
            notation += PieceType switch
            {
                PieceType.King => "K",
                PieceType.Queen => "Q",
                PieceType.Rook => "R",
                PieceType.Bishop => "B",
                PieceType.Knight => "N",
                _ => ""
            };
        }

        // For pawns capturing, include the file
        if (PieceType == PieceType.Pawn && SpecialMoveFlags.HasFlag(SpecialMoveType.Capture))
        {
            notation += From.File;
        }

        // Capture symbol
        if (SpecialMoveFlags.HasFlag(SpecialMoveType.Capture) || SpecialMoveFlags.HasFlag(SpecialMoveType.EnPassant))
        {
            notation += "x";
        }

        // Destination square
        notation += To.ToAlgebraic();

        // Promotion
        if (PromotionType.HasValue)
        {
            notation += "=";
            notation += PromotionType.Value switch
            {
                PieceType.Queen => "Q",
                PieceType.Rook => "R",
                PieceType.Bishop => "B",
                PieceType.Knight => "N",
                _ => ""
            };
        }

        // Check/Checkmate symbols
        if (SpecialMoveFlags.HasFlag(SpecialMoveType.Checkmate))
            notation += "#";
        else if (SpecialMoveFlags.HasFlag(SpecialMoveType.Check))
            notation += "+";

        return notation;
    }

    /// <summary>
    /// Returns true if this move is a capture
    /// </summary>
    public bool IsCapture => SpecialMoveFlags.HasFlag(SpecialMoveType.Capture) ||
                             SpecialMoveFlags.HasFlag(SpecialMoveType.EnPassant);

    /// <summary>
    /// Returns true if this move is a castling move
    /// </summary>
    public bool IsCastling => SpecialMoveFlags.HasFlag(SpecialMoveType.CastleKingside) ||
                              SpecialMoveFlags.HasFlag(SpecialMoveType.CastleQueenside);

    /// <summary>
    /// Returns true if this move is a pawn promotion
    /// </summary>
    public bool IsPromotion => SpecialMoveFlags.HasFlag(SpecialMoveType.PawnPromotion);

    /// <summary>
    /// Returns true if this move is en passant
    /// </summary>
    public bool IsEnPassant => SpecialMoveFlags.HasFlag(SpecialMoveType.EnPassant);

    /// <summary>
    /// Returns the row delta (positive for moving up the board from white's perspective)
    /// </summary>
    public int RowDelta => To.Row - From.Row;

    /// <summary>
    /// Returns the column delta (positive for moving right)
    /// </summary>
    public int ColDelta => To.Col - From.Col;

    /// <summary>
    /// Returns the absolute row distance
    /// </summary>
    public int RowDistance => Math.Abs(RowDelta);

    /// <summary>
    /// Returns the absolute column distance
    /// </summary>
    public int ColDistance => Math.Abs(ColDelta);

    /// <summary>
    /// Creates a copy of this move with additional special move flags
    /// </summary>
    public Move WithFlags(SpecialMoveType additionalFlags)
    {
        return this with { SpecialMoveFlags = SpecialMoveFlags | additionalFlags };
    }

    /// <summary>
    /// Creates a copy of this move with captured piece information
    /// </summary>
    public Move WithCapture(PieceType capturedPiece)
    {
        return this with
        {
            CapturedPiece = capturedPiece,
            SpecialMoveFlags = SpecialMoveFlags | SpecialMoveType.Capture
        };
    }

    /// <summary>
    /// Creates a copy of this move with server timestamp
    /// </summary>
    public Move WithServerTimestamp(long timestamp)
    {
        return this with { ServerTimestamp = timestamp };
    }

    public override string ToString()
    {
        return $"{PlayerColor} {PieceType}: {ToCoordinateNotation()}";
    }
}

/// <summary>
/// Represents a validated move with additional server-side information
/// </summary>
public sealed record ValidatedMove
{
    /// <summary>
    /// The original move request
    /// </summary>
    public required Move Move { get; init; }

    /// <summary>
    /// The result of validation
    /// </summary>
    public required MoveValidationResult ValidationResult { get; init; }

    /// <summary>
    /// Whether the move is valid
    /// </summary>
    public bool IsValid => ValidationResult == MoveValidationResult.Valid;

    /// <summary>
    /// Human-readable message about the validation result
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The game state after the move (if valid)
    /// </summary>
    public string? ResultingFen { get; init; }

    /// <summary>
    /// Whether this move results in check
    /// </summary>
    public bool ResultsInCheck { get; init; }

    /// <summary>
    /// Whether this move results in checkmate
    /// </summary>
    public bool ResultsInCheckmate { get; init; }

    /// <summary>
    /// Whether this move results in stalemate
    /// </summary>
    public bool ResultsInStalemate { get; init; }
}

/// <summary>
/// Represents a move request from the client to the server
/// This is what clients send - minimal information that the server validates
/// </summary>
public sealed record MoveRequest
{
    /// <summary>
    /// The game ID this move belongs to
    /// </summary>
    public required string GameId { get; init; }

    /// <summary>
    /// The player's session token for authentication
    /// </summary>
    public required string SessionToken { get; init; }

    /// <summary>
    /// Starting position
    /// </summary>
    public required Position From { get; init; }

    /// <summary>
    /// Destination position
    /// </summary>
    public required Position To { get; init; }

    /// <summary>
    /// Promotion piece type (if applicable)
    /// </summary>
    public PieceType? PromotionType { get; init; }

    /// <summary>
    /// Client-side timestamp for latency tracking
    /// </summary>
    public long ClientTimestamp { get; init; }

    /// <summary>
    /// Sequence number for move ordering
    /// </summary>
    public int SequenceNumber { get; init; }
}
