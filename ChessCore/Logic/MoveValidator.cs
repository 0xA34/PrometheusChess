using ChessCore.Models;

namespace ChessCore.Logic;

/// <summary>
/// Server-side move validation logic.
/// </summary>
public sealed class MoveValidator
{
    /// <summary>
    /// Validates a move request and returns the result
    /// </summary>
    /// <param name="board">The current board state</param>
    /// <param name="from">Starting position</param>
    /// <param name="to">Destination position</param>
    /// <param name="promotionType">Promotion piece type (for pawn promotion)</param>
    /// <param name="playerColor">The color of the player making the move</param>
    /// <returns>The validation result with details</returns>
    public ValidatedMove ValidateMove(Board board, Position from, Position to, PieceType? promotionType, PieceColor playerColor)
    {
        // Check if it's the player's turn
        if (board.CurrentTurn != playerColor)
        {
            return CreateInvalidResult(MoveValidationResult.NotYourTurn, "It's not your turn");
        }

        // Get the piece at the source position
        var piece = board.GetPieceAt(from);
        if (piece == null)
        {
            return CreateInvalidResult(MoveValidationResult.PieceNotFound, "No piece at the specified position");
        }

        // Check if the piece belongs to the player
        if (piece.Color != playerColor)
        {
            return CreateInvalidResult(MoveValidationResult.InvalidPiece, "That piece doesn't belong to you");
        }

        // Validate the move based on piece type
        var moveFlags = SpecialMoveType.None;
        var capturedPiece = board.GetPieceAt(to);

        if (!IsValidMoveForPiece(board, piece, from, to, out var specialFlags, out var validationMessage))
        {
            return CreateInvalidResult(MoveValidationResult.InvalidDestination, validationMessage ?? "Invalid move for this piece");
        }

        moveFlags |= specialFlags;

        // Check for captures
        if (capturedPiece != null)
        {
            if (capturedPiece.Color == playerColor)
            {
                return CreateInvalidResult(MoveValidationResult.InvalidDestination, "Cannot capture your own piece");
            }
            moveFlags |= SpecialMoveType.Capture;
        }

        // Handle pawn promotion
        if (piece.Type == PieceType.Pawn && IsPromotionRank(to, playerColor))
        {
            if (promotionType == null)
            {
                return CreateInvalidResult(MoveValidationResult.InvalidPromotion, "Promotion piece type required");
            }

            if (promotionType == PieceType.Pawn || promotionType == PieceType.King)
            {
                return CreateInvalidResult(MoveValidationResult.InvalidPromotion, "Invalid promotion piece type");
            }

            moveFlags |= SpecialMoveType.PawnPromotion;
        }

        // Simulate the move and check if it leaves the king in check
        var simulatedBoard = SimulateMove(board, from, to, promotionType);
        if (IsInCheck(simulatedBoard, playerColor))
        {
            return CreateInvalidResult(MoveValidationResult.WouldBeInCheck, "This move would leave your king in check");
        }

        // Check if the move results in check/checkmate/stalemate for opponent
        var opponentColor = playerColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
        bool isCheck = IsInCheck(simulatedBoard, opponentColor);
        bool isCheckmate = false;
        bool isStalemate = false;

        if (isCheck)
        {
            moveFlags |= SpecialMoveType.Check;
            if (!HasLegalMoves(simulatedBoard, opponentColor))
            {
                isCheckmate = true;
                moveFlags |= SpecialMoveType.Checkmate;
            }
        }
        else if (!HasLegalMoves(simulatedBoard, opponentColor))
        {
            isStalemate = true;
        }

        // Create the validated move
        var move = new Move(from, to, piece.Type, playerColor)
        {
            PromotionType = promotionType,
            SpecialMoveFlags = moveFlags,
            CapturedPiece = capturedPiece?.Type,
            ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return new ValidatedMove
        {
            Move = move,
            ValidationResult = MoveValidationResult.Valid,
            ResultingFen = simulatedBoard.ToFen(),
            ResultsInCheck = isCheck,
            ResultsInCheckmate = isCheckmate,
            ResultsInStalemate = isStalemate,
            Message = isCheckmate ? "Checkmate!" : (isCheck ? "Check!" : null)
        };
    }

    /// <summary>
    /// Validates if a move is valid for the specific piece type
    /// </summary>
    private bool IsValidMoveForPiece(Board board, ChessPiece piece, Position from, Position to,
        out SpecialMoveType specialFlags, out string? errorMessage)
    {
        specialFlags = SpecialMoveType.None;
        errorMessage = null;

        if (!to.IsValid)
        {
            errorMessage = "Destination is off the board";
            return false;
        }

        if (from == to)
        {
            errorMessage = "Cannot move to the same square";
            return false;
        }

        return piece.Type switch
        {
            PieceType.Pawn => IsValidPawnMove(board, piece, from, to, out specialFlags, out errorMessage),
            PieceType.Knight => IsValidKnightMove(from, to, out errorMessage),
            PieceType.Bishop => IsValidBishopMove(board, from, to, out errorMessage),
            PieceType.Rook => IsValidRookMove(board, from, to, out errorMessage),
            PieceType.Queen => IsValidQueenMove(board, from, to, out errorMessage),
            PieceType.King => IsValidKingMove(board, piece, from, to, out specialFlags, out errorMessage),
            _ => false
        };
    }

    #region Piece-Specific Validation

    private bool IsValidPawnMove(Board board, ChessPiece pawn, Position from, Position to,
        out SpecialMoveType specialFlags, out string? errorMessage)
    {
        specialFlags = SpecialMoveType.None;
        errorMessage = null;

        int direction = pawn.Color == PieceColor.White ? 1 : -1;
        int startRow = pawn.Color == PieceColor.White ? 1 : 6;
        int rowDelta = to.Row - from.Row;
        int colDelta = Math.Abs(to.Col - from.Col);

        // Forward move (1 square)
        if (colDelta == 0 && rowDelta == direction)
        {
            if (board.GetPieceAt(to) != null)
            {
                errorMessage = "Cannot move forward - square is occupied";
                return false;
            }
            return true;
        }

        // Forward move (2 squares from starting position)
        if (colDelta == 0 && rowDelta == 2 * direction && from.Row == startRow)
        {
            var intermediatePos = new Position(from.Row + direction, from.Col);
            if (board.GetPieceAt(intermediatePos) != null || board.GetPieceAt(to) != null)
            {
                errorMessage = "Cannot move forward - path is blocked";
                return false;
            }
            specialFlags |= SpecialMoveType.DoublePawnPush;
            return true;
        }

        // Diagonal capture
        if (colDelta == 1 && rowDelta == direction)
        {
            var targetPiece = board.GetPieceAt(to);

            // Normal capture
            if (targetPiece != null && targetPiece.Color != pawn.Color)
            {
                return true;
            }

            // En passant
            if (board.EnPassantTarget.HasValue && board.EnPassantTarget.Value == to)
            {
                specialFlags |= SpecialMoveType.EnPassant;
                return true;
            }

            errorMessage = "Invalid pawn capture - no piece to capture";
            return false;
        }

        errorMessage = "Invalid pawn move";
        return false;
    }

    private bool IsValidKnightMove(Position from, Position to, out string? errorMessage)
    {
        errorMessage = null;
        int rowDelta = Math.Abs(to.Row - from.Row);
        int colDelta = Math.Abs(to.Col - from.Col);

        // Knight moves in an "L" shape: 2+1 or 1+2
        bool isValid = (rowDelta == 2 && colDelta == 1) || (rowDelta == 1 && colDelta == 2);

        if (!isValid)
        {
            errorMessage = "Knight must move in an L-shape (2+1 squares)";
        }

        return isValid;
    }

    private bool IsValidBishopMove(Board board, Position from, Position to, out string? errorMessage)
    {
        errorMessage = null;

        if (!from.IsSameDiagonal(to))
        {
            errorMessage = "Bishop must move diagonally";
            return false;
        }

        if (!IsPathClear(board, from, to))
        {
            errorMessage = "Path is blocked";
            return false;
        }

        return true;
    }

    private bool IsValidRookMove(Board board, Position from, Position to, out string? errorMessage)
    {
        errorMessage = null;

        if (!from.IsSameRow(to) && !from.IsSameCol(to))
        {
            errorMessage = "Rook must move horizontally or vertically";
            return false;
        }

        if (!IsPathClear(board, from, to))
        {
            errorMessage = "Path is blocked";
            return false;
        }

        return true;
    }

    private bool IsValidQueenMove(Board board, Position from, Position to, out string? errorMessage)
    {
        errorMessage = null;

        bool isDiagonal = from.IsSameDiagonal(to);
        bool isStraight = from.IsSameRow(to) || from.IsSameCol(to);

        if (!isDiagonal && !isStraight)
        {
            errorMessage = "Queen must move diagonally, horizontally, or vertically";
            return false;
        }

        if (!IsPathClear(board, from, to))
        {
            errorMessage = "Path is blocked";
            return false;
        }

        return true;
    }

    private bool IsValidKingMove(Board board, ChessPiece king, Position from, Position to,
        out SpecialMoveType specialFlags, out string? errorMessage)
    {
        specialFlags = SpecialMoveType.None;
        errorMessage = null;

        int rowDelta = Math.Abs(to.Row - from.Row);
        int colDelta = Math.Abs(to.Col - from.Col);

        // Normal king move (1 square in any direction)
        if (rowDelta <= 1 && colDelta <= 1)
        {
            return true;
        }

        // Castling
        if (rowDelta == 0 && colDelta == 2)
        {
            return IsValidCastling(board, king, from, to, out specialFlags, out errorMessage);
        }

        errorMessage = "King can only move one square in any direction (or castle)";
        return false;
    }

    private bool IsValidCastling(Board board, ChessPiece king, Position from, Position to,
        out SpecialMoveType specialFlags, out string? errorMessage)
    {
        specialFlags = SpecialMoveType.None;
        errorMessage = null;

        // King must not have moved
        if (king.HasMoved)
        {
            errorMessage = "Cannot castle - king has already moved";
            return false;
        }

        // Determine if kingside or queenside
        bool isKingside = to.Col > from.Col;
        var castlingRights = board.GetCastlingRights(king.Color);

        if (isKingside && !castlingRights.HasFlag(CastlingRights.Kingside))
        {
            errorMessage = "Cannot castle kingside - no castling rights";
            return false;
        }

        if (!isKingside && !castlingRights.HasFlag(CastlingRights.Queenside))
        {
            errorMessage = "Cannot castle queenside - no castling rights";
            return false;
        }

        // Check rook position and status
        int rookCol = isKingside ? 7 : 0;
        var rookPos = new Position(from.Row, rookCol);
        var rook = board.GetPieceAt(rookPos);

        if (rook == null || rook.Type != PieceType.Rook || rook.Color != king.Color)
        {
            errorMessage = "Cannot castle - rook is not in position";
            return false;
        }

        if (rook.HasMoved)
        {
            errorMessage = "Cannot castle - rook has already moved";
            return false;
        }

        // Check if path is clear
        int startCol = Math.Min(from.Col, rookCol) + 1;
        int endCol = Math.Max(from.Col, rookCol);
        for (int col = startCol; col < endCol; col++)
        {
            if (board.GetPieceAt(from.Row, col) != null)
            {
                errorMessage = "Cannot castle - path is blocked";
                return false;
            }
        }

        // King cannot castle out of, through, or into check
        if (IsInCheck(board, king.Color))
        {
            errorMessage = "Cannot castle while in check";
            return false;
        }

        // Check squares the king passes through
        int direction = isKingside ? 1 : -1;
        for (int i = 1; i <= 2; i++)
        {
            var throughPos = new Position(from.Row, from.Col + (direction * i));
            var tempBoard = board.Clone();
            tempBoard.MovePieceInternal(from, throughPos);
            if (IsInCheck(tempBoard, king.Color))
            {
                errorMessage = "Cannot castle through check";
                return false;
            }
        }

        specialFlags = isKingside ? SpecialMoveType.CastleKingside : SpecialMoveType.CastleQueenside;
        return true;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if the path between two positions is clear (for sliding pieces)
    /// </summary>
    private bool IsPathClear(Board board, Position from, Position to)
    {
        int rowDirection = Math.Sign(to.Row - from.Row);
        int colDirection = Math.Sign(to.Col - from.Col);

        int currentRow = from.Row + rowDirection;
        int currentCol = from.Col + colDirection;

        while (currentRow != to.Row || currentCol != to.Col)
        {
            if (board.GetPieceAt(currentRow, currentCol) != null)
            {
                return false;
            }

            currentRow += rowDirection;
            currentCol += colDirection;
        }

        return true;
    }

    /// <summary>
    /// Checks if a position is the promotion rank for a pawn
    /// </summary>
    private bool IsPromotionRank(Position to, PieceColor color)
    {
        return (color == PieceColor.White && to.Row == 7) ||
               (color == PieceColor.Black && to.Row == 0);
    }

    /// <summary>
    /// Simulates a move on a copy of the board
    /// </summary>
    public Board SimulateMove(Board board, Position from, Position to, PieceType? promotionType)
    {
        var simulatedBoard = board.Clone();
        var piece = simulatedBoard.GetPieceAt(from);

        if (piece == null)
            return simulatedBoard;

        // Handle en passant capture
        if (piece.Type == PieceType.Pawn && board.EnPassantTarget.HasValue && to == board.EnPassantTarget.Value)
        {
            int capturedPawnRow = piece.Color == PieceColor.White ? to.Row - 1 : to.Row + 1;
            simulatedBoard.RemovePiece(new Position(capturedPawnRow, to.Col));
        }

        // Handle castling
        if (piece.Type == PieceType.King && Math.Abs(to.Col - from.Col) == 2)
        {
            bool isKingside = to.Col > from.Col;
            int rookFromCol = isKingside ? 7 : 0;
            int rookToCol = isKingside ? 5 : 3;
            simulatedBoard.MovePieceInternal(new Position(from.Row, rookFromCol), new Position(from.Row, rookToCol));
        }

        // Perform the move
        simulatedBoard.MovePieceInternal(from, to);

        // Handle promotion
        if (piece.Type == PieceType.Pawn && promotionType.HasValue && IsPromotionRank(to, piece.Color))
        {
            var promotedPiece = simulatedBoard.RemovePiece(to);
            if (promotedPiece != null)
            {
                var newPiece = new ChessPiece(promotionType.Value, piece.Color, to);
                simulatedBoard.PlacePiece(newPiece);
            }
        }

        // Update en passant target
        if (piece.Type == PieceType.Pawn && Math.Abs(to.Row - from.Row) == 2)
        {
            int epRow = piece.Color == PieceColor.White ? from.Row + 1 : from.Row - 1;
            simulatedBoard.SetEnPassantTarget(new Position(epRow, from.Col));
        }
        else
        {
            simulatedBoard.SetEnPassantTarget(null);
        }

        // Update castling rights
        simulatedBoard.UpdateCastlingRights(piece, from);

        // Switch turn
        simulatedBoard.SwitchTurn();

        return simulatedBoard;
    }

    /// <summary>
    /// Checks if the specified color's king is in check
    /// </summary>
    public bool IsInCheck(Board board, PieceColor color)
    {
        var king = board.FindKing(color);
        if (king == null)
            return false;

        var opponentColor = color == PieceColor.White ? PieceColor.Black : PieceColor.White;

        // Check if any opponent piece can attack the king's position
        foreach (var piece in board.GetPieces(opponentColor))
        {
            if (CanAttack(board, piece, king.Position))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a piece can attack a target position (for check detection)
    /// </summary>
    private bool CanAttack(Board board, ChessPiece piece, Position target)
    {
        var from = piece.Position;

        return piece.Type switch
        {
            PieceType.Pawn => CanPawnAttack(piece, target),
            PieceType.Knight => CanKnightAttack(from, target),
            PieceType.Bishop => CanBishopAttack(board, from, target),
            PieceType.Rook => CanRookAttack(board, from, target),
            PieceType.Queen => CanQueenAttack(board, from, target),
            PieceType.King => CanKingAttack(from, target),
            _ => false
        };
    }

    private bool CanPawnAttack(ChessPiece pawn, Position target)
    {
        int direction = pawn.Color == PieceColor.White ? 1 : -1;
        int rowDelta = target.Row - pawn.Position.Row;
        int colDelta = Math.Abs(target.Col - pawn.Position.Col);

        // Pawns attack diagonally
        return rowDelta == direction && colDelta == 1;
    }

    private bool CanKnightAttack(Position from, Position target)
    {
        int rowDelta = Math.Abs(target.Row - from.Row);
        int colDelta = Math.Abs(target.Col - from.Col);
        return (rowDelta == 2 && colDelta == 1) || (rowDelta == 1 && colDelta == 2);
    }

    private bool CanBishopAttack(Board board, Position from, Position target)
    {
        if (!from.IsSameDiagonal(target))
            return false;
        return IsPathClear(board, from, target);
    }

    private bool CanRookAttack(Board board, Position from, Position target)
    {
        if (!from.IsSameRow(target) && !from.IsSameCol(target))
            return false;
        return IsPathClear(board, from, target);
    }

    private bool CanQueenAttack(Board board, Position from, Position target)
    {
        bool isDiagonal = from.IsSameDiagonal(target);
        bool isStraight = from.IsSameRow(target) || from.IsSameCol(target);

        if (!isDiagonal && !isStraight)
            return false;

        return IsPathClear(board, from, target);
    }

    private bool CanKingAttack(Position from, Position target)
    {
        int rowDelta = Math.Abs(target.Row - from.Row);
        int colDelta = Math.Abs(target.Col - from.Col);
        return rowDelta <= 1 && colDelta <= 1;
    }

    /// <summary>
    /// Checks if a player has any legal moves
    /// </summary>
    public bool HasLegalMoves(Board board, PieceColor color)
    {
        foreach (var piece in board.GetPieces(color))
        {
            var legalMoves = GetLegalMoves(board, piece);
            if (legalMoves.Any())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all legal moves for a piece
    /// </summary>
    public IEnumerable<Position> GetLegalMoves(Board board, ChessPiece piece)
    {
        var possibleMoves = GetPossibleMoves(board, piece);

        foreach (var to in possibleMoves)
        {
            // Check if move is valid for the piece type
            if (!IsValidMoveForPiece(board, piece, piece.Position, to, out _, out _))
                continue;

            // Check if destination has own piece
            var destPiece = board.GetPieceAt(to);
            if (destPiece != null && destPiece.Color == piece.Color)
                continue;

            // Check if move leaves king in check
            var simulatedBoard = SimulateMove(board, piece.Position, to, null);
            if (IsInCheck(simulatedBoard, piece.Color))
                continue;

            yield return to;
        }
    }

    /// <summary>
    /// Gets all possible destination squares for a piece (without validation)
    /// </summary>
    private IEnumerable<Position> GetPossibleMoves(Board board, ChessPiece piece)
    {
        var from = piece.Position;

        switch (piece.Type)
        {
            case PieceType.Pawn:
                foreach (var move in GetPawnMoves(board, piece))
                    yield return move;
                break;

            case PieceType.Knight:
                foreach (var move in GetKnightMoves(from))
                    yield return move;
                break;

            case PieceType.Bishop:
                foreach (var move in GetSlidingMoves(from, new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) }))
                    yield return move;
                break;

            case PieceType.Rook:
                foreach (var move in GetSlidingMoves(from, new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }))
                    yield return move;
                break;

            case PieceType.Queen:
                foreach (var move in GetSlidingMoves(from, new[] { (-1, -1), (-1, 1), (1, -1), (1, 1), (-1, 0), (1, 0), (0, -1), (0, 1) }))
                    yield return move;
                break;

            case PieceType.King:
                foreach (var move in GetKingMoves(from))
                    yield return move;
                break;
        }
    }

    private IEnumerable<Position> GetPawnMoves(Board board, ChessPiece pawn)
    {
        int direction = pawn.Color == PieceColor.White ? 1 : -1;
        int startRow = pawn.Color == PieceColor.White ? 1 : 6;
        var from = pawn.Position;

        // Forward moves
        yield return from.Offset(direction, 0);
        if (from.Row == startRow)
            yield return from.Offset(2 * direction, 0);

        // Captures
        yield return from.Offset(direction, -1);
        yield return from.Offset(direction, 1);

        // En passant
        if (board.EnPassantTarget.HasValue)
            yield return board.EnPassantTarget.Value;
    }

    private IEnumerable<Position> GetKnightMoves(Position from)
    {
        int[] rowDeltas = { -2, -2, -1, -1, 1, 1, 2, 2 };
        int[] colDeltas = { -1, 1, -2, 2, -2, 2, -1, 1 };

        for (int i = 0; i < 8; i++)
        {
            var to = from.Offset(rowDeltas[i], colDeltas[i]);
            if (to.IsValid)
                yield return to;
        }
    }

    private IEnumerable<Position> GetSlidingMoves(Position from, (int, int)[] directions)
    {
        foreach (var (rowDir, colDir) in directions)
        {
            for (int i = 1; i <= 7; i++)
            {
                var to = from.Offset(rowDir * i, colDir * i);
                if (to.IsValid)
                    yield return to;
            }
        }
    }

    private IEnumerable<Position> GetKingMoves(Position from)
    {
        for (int rowDelta = -1; rowDelta <= 1; rowDelta++)
        {
            for (int colDelta = -1; colDelta <= 1; colDelta++)
            {
                if (rowDelta == 0 && colDelta == 0)
                    continue;

                var to = from.Offset(rowDelta, colDelta);
                if (to.IsValid)
                    yield return to;
            }
        }

        // Castling
        yield return from.Offset(0, 2);  // Kingside
        yield return from.Offset(0, -2); // Queenside
    }

    /// <summary>
    /// Checks for insufficient material (automatic draw)
    /// </summary>
    public bool IsInsufficientMaterial(Board board)
    {
        var whitePieces = board.WhitePieces.ToList();
        var blackPieces = board.BlackPieces.ToList();

        // King vs King
        if (whitePieces.Count == 1 && blackPieces.Count == 1)
            return true;

        // King + Bishop vs King
        if ((whitePieces.Count == 2 && blackPieces.Count == 1 && whitePieces.Any(p => p.Type == PieceType.Bishop)) ||
            (whitePieces.Count == 1 && blackPieces.Count == 2 && blackPieces.Any(p => p.Type == PieceType.Bishop)))
            return true;

        // King + Knight vs King
        if ((whitePieces.Count == 2 && blackPieces.Count == 1 && whitePieces.Any(p => p.Type == PieceType.Knight)) ||
            (whitePieces.Count == 1 && blackPieces.Count == 2 && blackPieces.Any(p => p.Type == PieceType.Knight)))
            return true;

        // King + Bishop vs King + Bishop (same color bishops)
        if (whitePieces.Count == 2 && blackPieces.Count == 2)
        {
            var whiteBishop = whitePieces.FirstOrDefault(p => p.Type == PieceType.Bishop);
            var blackBishop = blackPieces.FirstOrDefault(p => p.Type == PieceType.Bishop);

            if (whiteBishop != null && blackBishop != null)
            {
                // Both bishops on same color squares
                if (whiteBishop.Position.IsLightSquare == blackBishop.Position.IsLightSquare)
                    return true;
            }
        }

        return false;
    }

    #endregion
    

    private static ValidatedMove CreateInvalidResult(MoveValidationResult result, string message)
    {
        return new ValidatedMove
        {
            Move = new Move(),
            ValidationResult = result,
            Message = message
        };
    }
}
