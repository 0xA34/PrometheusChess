using System.Numerics;
using ChessCore.Models;
using ImGuiNET;
using PrometheusVulkan.Core;
using PrometheusVulkan.State;
using PrometheusVulkan.UI;

namespace PrometheusVulkan.UI.Screens;

public class GameScreen : IScreen
{
    private readonly GameManager _gameManager;
    private readonly UIManager _uiManager;
    private readonly ResourceManager _resourceManager;

    // Interaction State
    private int _selectedSquare = -1;
    private List<int> _legalMoveSquares = new();
    
    // Promotion
    private bool _showPromotionDialog;
    private string _promotionFrom = "";
    private string _promotionTo = "";

    public GameScreen(GameManager gameManager, UIManager uiManager, ResourceManager resourceManager)
    {
        _gameManager = gameManager;
        _uiManager = uiManager;
        _resourceManager = resourceManager;
    }

    public void OnShow()
    {
        _selectedSquare = -1;
        _legalMoveSquares.Clear();
        _showPromotionDialog = false;
    }

    public void OnHide()
    {
    }

    public void Update(float deltaTime)
    {
    }

    public void Render()
    {
        var io = ImGui.GetIO();
        var windowSize = io.DisplaySize;

        // Layout constants
        float headerHeight = 90f;
        float sidePanelWidth = 320f;
        float boardMargin = 24f;
        
        // Calculate board size to fit nicely
        float availableWidth = windowSize.X - sidePanelWidth - boardMargin * 3;
        float availableHeight = windowSize.Y - headerHeight - boardMargin * 2;
        float boardSize = Math.Min(availableWidth, availableHeight);
        boardSize = Math.Max(boardSize, 400f); // Minimum size
        
        // Center the board in its available space
        float boardX = boardMargin + (availableWidth - boardSize) / 2;
        float boardY = headerHeight + (availableHeight - boardSize) / 2;

        // Render components
        RenderHeader(windowSize, headerHeight);
        RenderBoard(new Vector2(boardX, boardY), boardSize);
        RenderSidePanel(windowSize, sidePanelWidth, headerHeight, boardY);

        // Overlays
        if (_gameManager.CurrentState == GameState.GameOver)
        {
            RenderGameOverPanel(windowSize);
        }
        
        if (_showPromotionDialog)
        {
            RenderPromotionDialog(windowSize);
        }
    }

    private void RenderHeader(Vector2 windowSize, float headerHeight)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(windowSize.X, headerHeight));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeManager.PanelBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        
        if (ImGui.Begin("##GameHeader", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            var drawList = ImGui.GetWindowDrawList();
            float thirdWidth = windowSize.X / 3;
            bool isMyTurn = _gameManager.IsMyTurn();
            
            // === LEFT SECTION: Opponent ===
            float leftPadding = 32;
            ImGui.SetCursorPos(new Vector2(leftPadding, 20));
            ImGui.BeginGroup();
            
            // Opponent clock box
            string oppTime = _gameManager.FormatTime(_gameManager.GetOpponentTimeMs());
            var oppTimeSize = ImGui.CalcTextSize(oppTime);
            Vector2 oppBoxPos = new Vector2(leftPadding, 18);
            Vector2 oppBoxSize = new Vector2(110, 40);
            drawList.AddRectFilled(oppBoxPos, oppBoxPos + oppBoxSize, 
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.17f, 0.22f, 1.0f)), 6.0f);
            drawList.AddText(oppBoxPos + (oppBoxSize - oppTimeSize) / 2, 
                ImGui.ColorConvertFloat4ToU32(ThemeManager.TextWhite), oppTime);
            
            ImGui.SetCursorPos(new Vector2(leftPadding + oppBoxSize.X + 16, 28));
            string oppLabel = $"{_gameManager.OpponentName ?? "Opponent"} ({_gameManager.OpponentRating})";
            ImGui.TextColored(ThemeManager.TextMuted, oppLabel);
            
            ImGui.EndGroup();
            
            // === CENTER SECTION: Turn Indicator ===
            string turnText = isMyTurn ? "YOUR TURN" : "WAITING...";
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            float turnWidth = ImGui.CalcTextSize(turnText).X;
            float centerX = (windowSize.X - turnWidth) / 2;
            ImGui.SetCursorPos(new Vector2(centerX, (headerHeight - 30) / 2));
            ImGui.TextColored(isMyTurn ? ThemeManager.PrimaryOrange : ThemeManager.TextMuted, turnText);
            ImGui.PopFont();
            
            // === RIGHT SECTION: You ===
            float rightPadding = 32;
            string myTime = _gameManager.FormatTime(_gameManager.GetMyTimeMs());
            var myTimeSize = ImGui.CalcTextSize(myTime);
            Vector2 myBoxPos = new Vector2(windowSize.X - rightPadding - 110, 18);
            Vector2 myBoxSize = new Vector2(110, 40);
            
            var myBoxColor = isMyTurn ? ThemeManager.PrimaryOrange : new Vector4(0.15f, 0.17f, 0.22f, 1.0f);
            drawList.AddRectFilled(myBoxPos, myBoxPos + myBoxSize, 
                ImGui.ColorConvertFloat4ToU32(myBoxColor), 6.0f);
            
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
            var bigTimeSize = ImGui.CalcTextSize(myTime);
            ImGui.PopFont();
            drawList.AddText(ImGui.GetIO().Fonts.Fonts[0], ImGui.GetIO().Fonts.Fonts[0].FontSize,
                myBoxPos + (myBoxSize - bigTimeSize) / 2, 
                ImGui.ColorConvertFloat4ToU32(isMyTurn ? ThemeManager.BackgroundDarker : ThemeManager.TextWhite), myTime);
            
            string myLabel = $"{_gameManager.Username ?? "You"} ({_gameManager.Rating})";
            float myLabelWidth = ImGui.CalcTextSize(myLabel).X;
            ImGui.SetCursorPos(new Vector2(myBoxPos.X - myLabelWidth - 16, 28));
            ImGui.TextColored(ThemeManager.PrimaryOrange, myLabel);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        
        // Bottom border
        var bgDrawList = ImGui.GetBackgroundDrawList();
        bgDrawList.AddLine(
            new Vector2(0, headerHeight),
            new Vector2(windowSize.X, headerHeight),
            ImGui.ColorConvertFloat4ToU32(ThemeManager.PanelBorder),
            2.0f
        );
    }

    private void RenderBoard(Vector2 boardPos, float boardSize)
    {
        float squareSize = boardSize / 8.0f;

        ImGui.SetNextWindowPos(boardPos);
        ImGui.SetNextWindowSize(new Vector2(boardSize, boardSize));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        
        if (ImGui.Begin("##ChessBoard", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar))
        {
            var drawList = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            
            bool isWhitePOV = _gameManager.PlayerColor == PieceColor.White;

            for (int rank = 0; rank < 8; rank++)
            {
                for (int file = 0; file < 8; file++)
                {
                    int visualRank = rank; 
                    int visualFile = file;

                    int logicalRank = isWhitePOV ? (7 - visualRank) : visualRank;
                    int logicalFile = isWhitePOV ? visualFile : (7 - visualFile);
                    
                    int squareIndex = logicalRank * 8 + logicalFile;
                    
                    Vector2 sqPos = new Vector2(p.X + visualFile * squareSize, p.Y + visualRank * squareSize);
                    
                    // Square color
                    bool isLight = (logicalRank + logicalFile) % 2 != 0;
                    var color = isLight ? ThemeManager.LightSquareColor : ThemeManager.DarkSquareColor;
                    
                    // Highlight Last Move
                    if (_gameManager.MoveHistory.Count > 0)
                    {
                        var lastMove = _gameManager.MoveHistory.Last();
                        if (lastMove.From.CalculateIndex() == squareIndex || lastMove.To.CalculateIndex() == squareIndex)
                        {
                            color = Vector4.Lerp(color, ThemeManager.LastMoveColor, 0.5f);
                        }
                    }
                    
                    // Highlight Selection
                    if (_selectedSquare == squareIndex)
                    {
                        color = Vector4.Lerp(color, ThemeManager.SelectedSquareColor, 0.6f);
                    }
                    
                    drawList.AddRectFilled(sqPos, sqPos + new Vector2(squareSize, squareSize), ImGui.ColorConvertFloat4ToU32(color));
                    
                    // Coordinates
                    uint coordColor = ImGui.ColorConvertFloat4ToU32(isLight ? ThemeManager.DarkSquareColor : ThemeManager.LightSquareColor);
                    if (isWhitePOV)
                    {
                        if (visualRank == 7)
                            drawList.AddText(sqPos + new Vector2(squareSize - 10, squareSize - 14), coordColor, ((char)('a' + logicalFile)).ToString());
                        if (visualFile == 0)
                            drawList.AddText(sqPos + new Vector2(3, 2), coordColor, (logicalRank + 1).ToString());
                    }
                    else
                    {
                        if (visualRank == 7)
                            drawList.AddText(sqPos + new Vector2(squareSize - 10, squareSize - 14), coordColor, ((char)('a' + logicalFile)).ToString());
                        if (visualFile == 7)
                            drawList.AddText(sqPos + new Vector2(3, 2), coordColor, (logicalRank + 1).ToString());
                    }

                    // Draw Piece
                    if (_gameManager.CurrentBoard != null)
                    {
                        var pos = PositionExtensions.FromIndex(squareIndex);
                        var piece = _gameManager.CurrentBoard.GetPieceAt(pos);
                        if (piece != null)
                        {
                            var tex = _resourceManager.GetPieceTexture(piece.Type, piece.Color);
                            if (tex != IntPtr.Zero)
                            {
                                float pieceSize = squareSize * 0.9f;
                                float offset = (squareSize - pieceSize) / 2;
                                drawList.AddImage(tex, sqPos + new Vector2(offset), sqPos + new Vector2(offset + pieceSize));
                            }
                            else
                            {
                                // Text Fallback
                                string symbol = GetPieceSymbol(piece.Type, piece.Color);
                                var textSize = ImGui.CalcTextSize(symbol);
                                drawList.AddText(sqPos + (new Vector2(squareSize) - textSize) / 2, 
                                    piece.Color == PieceColor.White ? 0xFFFFFFFF : 0xFF000000, symbol);
                            }
                        }
                    }
                    
                    // Legal Move Hints
                    if (_legalMoveSquares.Contains(squareIndex))
                    {
                        var capPos = PositionExtensions.FromIndex(squareIndex);
                        bool isCapture = _gameManager.CurrentBoard?.GetPieceAt(capPos) != null;
                        uint hintColor = ImGui.ColorConvertFloat4ToU32(ThemeManager.LegalMoveColor);
                        
                        if (isCapture)
                        {
                            drawList.AddCircle(sqPos + new Vector2(squareSize/2), squareSize/2 - 3, hintColor, 24, 3.5f);
                        }
                        else
                        {
                            drawList.AddCircleFilled(sqPos + new Vector2(squareSize/2), squareSize/6, hintColor);
                        }
                    }

                    // Click detection
                    ImGui.SetCursorScreenPos(sqPos);
                    ImGui.PushID(squareIndex);
                    if (ImGui.InvisibleButton("sq", new Vector2(squareSize, squareSize)))
                    {
                        HandleSquareClick(squareIndex);
                    }
                    ImGui.PopID();
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private string GetPieceSymbol(PieceType type, PieceColor color)
    {
        return type switch
        {
            PieceType.King => color == PieceColor.White ? "♔" : "♚",
            PieceType.Queen => color == PieceColor.White ? "♕" : "♛",
            PieceType.Rook => color == PieceColor.White ? "♖" : "♜",
            PieceType.Bishop => color == PieceColor.White ? "♗" : "♝",
            PieceType.Knight => color == PieceColor.White ? "♘" : "♞",
            PieceType.Pawn => color == PieceColor.White ? "♙" : "♟",
            _ => "?"
        };
    }

    private void RenderSidePanel(Vector2 windowSize, float panelWidth, float headerHeight, float boardY)
    {
        float panelX = windowSize.X - panelWidth - 16;
        float panelHeight = windowSize.Y - headerHeight - 32;
        
        ImGui.SetNextWindowPos(new Vector2(panelX, headerHeight + 16));
        ImGui.SetNextWindowSize(new Vector2(panelWidth, panelHeight));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ThemeManager.PanelBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 16));
        
        if (ImGui.Begin("##SidePanel", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            // Player Names Section
            ImGui.TextColored(ThemeManager.TextHighlight, "PLAYERS");
            ImGui.Separator();
            ImGui.Spacing();
            
            // You
            ImGui.TextColored(ThemeManager.PrimaryOrange, $"► {_gameManager.Username}");
            ImGui.SameLine();
            ImGui.TextColored(ThemeManager.TextMuted, $"({_gameManager.Rating})");
            
            ImGui.Spacing();
            
            // Opponent
            ImGui.TextColored(ThemeManager.TextWhite, $"  {_gameManager.OpponentName}");
            ImGui.SameLine();
            ImGui.TextColored(ThemeManager.TextMuted, $"({_gameManager.OpponentRating})");
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            // Actions Section
            ImGui.TextColored(ThemeManager.TextHighlight, "ACTIONS");
            ImGui.Separator();
            ImGui.Spacing();
            
            ThemeManager.PushOverwatchButtonStyle(false);
            if (ImGui.Button("OFFER DRAW", new Vector2(-1, 42)))
            {
                _ = _gameManager.OfferDrawAsync();
            }
            ThemeManager.PopOverwatchButtonStyle();
            
            ImGui.Spacing();
            
            ThemeManager.PushDangerButtonStyle();
            if (ImGui.Button("RESIGN", new Vector2(-1, 42)))
            {
                _ = _gameManager.ResignAsync();
            }
            ThemeManager.PopOverwatchButtonStyle();
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            // Move History Section
            ImGui.TextColored(ThemeManager.TextHighlight, "MOVE HISTORY");
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.BeginChild("##MoveHistory", new Vector2(-1, -1), ImGuiChildFlags.None);
            int moveNum = 1;
            for (int i = 0; i < _gameManager.MoveHistory.Count; i += 2)
            {
                string white = _gameManager.MoveHistory[i].ToString();
                string black = (i + 1 < _gameManager.MoveHistory.Count) ? _gameManager.MoveHistory[i + 1].ToString() : "";
                ImGui.Text($"{moveNum}. {white,-8} {black}");
                moveNum++;
            }
            ImGui.EndChild();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void HandleSquareClick(int squareIndex)
    {
        if (_selectedSquare == -1)
        {
            var clickPos = PositionExtensions.FromIndex(squareIndex);
            var piece = _gameManager.CurrentBoard?.GetPieceAt(clickPos);
            if (piece != null && piece.Color == _gameManager.PlayerColor)
            {
                _selectedSquare = squareIndex;
                CalculateLegalMoves(squareIndex);
            }
        }
        else
        {
            if (_selectedSquare == squareIndex)
            {
                _selectedSquare = -1;
                _legalMoveSquares.Clear();
                return;
            }

            if (_legalMoveSquares.Contains(squareIndex))
            {
                var fromStr = PositionExtensions.FromIndex(_selectedSquare).ToAlgebraic();
                var toStr = PositionExtensions.FromIndex(squareIndex).ToAlgebraic();
                
                if (IsPromotionMove(_selectedSquare, squareIndex))
                {
                    _promotionFrom = fromStr;
                    _promotionTo = toStr;
                    _showPromotionDialog = true;
                }
                else
                {
                    _ = _gameManager.SendMoveAsync(fromStr, toStr);
                    _selectedSquare = -1;
                    _legalMoveSquares.Clear();
                }
            }
            else
            {
                var switchPos = PositionExtensions.FromIndex(squareIndex);
                var piece = _gameManager.CurrentBoard?.GetPieceAt(switchPos);
                if (piece != null && piece.Color == _gameManager.PlayerColor)
                {
                    _selectedSquare = squareIndex;
                    CalculateLegalMoves(squareIndex);
                }
                else
                {
                    _selectedSquare = -1;
                    _legalMoveSquares.Clear();
                }
            }
        }
    }
    
    private void CalculateLegalMoves(int fromIndex)
    {
        _legalMoveSquares.Clear();
        var piece = _gameManager.CurrentBoard?.GetPieceAt(PositionExtensions.FromIndex(fromIndex));
        if (piece == null) return;

        var validator = new ChessCore.Logic.MoveValidator();
        var moves = validator.GetLegalMoves(_gameManager.CurrentBoard, piece);
        foreach (var pos in moves)
        {
            _legalMoveSquares.Add(pos.CalculateIndex());
        }
    }
    
    private bool IsPromotionMove(int fromIndex, int toIndex)
    {
        var fromPos = PositionExtensions.FromIndex(fromIndex);
        var piece = _gameManager.CurrentBoard?.GetPieceAt(fromPos);
        if (piece?.Type != PieceType.Pawn) return false;
         
        int toRank = toIndex / 8;
        return (_gameManager.PlayerColor == PieceColor.White && toRank == 7) ||
               (_gameManager.PlayerColor == PieceColor.Black && toRank == 0);
    }

    private void RenderPromotionDialog(Vector2 windowSize)
    {
        // Dim background
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(windowSize);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0.75f));
        
        if (ImGui.Begin("##PromoDim", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            // Centered dialog
            float dialogWidth = 320f;
            float dialogHeight = 220f;
            
            ImGui.SetCursorPos(new Vector2((windowSize.X - dialogWidth) / 2, (windowSize.Y - dialogHeight) / 2));
            
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeManager.PanelBg);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
            
            if (ImGui.BeginChild("##PromoDialog", new Vector2(dialogWidth, dialogHeight), ImGuiChildFlags.Borders))
            {
                ImGui.Spacing();
                
                string title = "CHOOSE PROMOTION";
                float titleW = ImGui.CalcTextSize(title).X;
                ImGui.SetCursorPosX((dialogWidth - titleW) / 2);
                ImGui.TextColored(ThemeManager.PrimaryOrange, title);
                
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                
                ThemeManager.PushOverwatchButtonStyle(false);
                PieceType[] types = new[] { PieceType.Queen, PieceType.Rook, PieceType.Bishop, PieceType.Knight };
                foreach (var t in types)
                {
                    if (ImGui.Button(t.ToString().ToUpper(), new Vector2(-1, 36)))
                    {
                        _ = _gameManager.SendMoveAsync(_promotionFrom, _promotionTo, t);
                        _showPromotionDialog = false;
                        _selectedSquare = -1;
                        _legalMoveSquares.Clear();
                    }
                }
                ThemeManager.PopOverwatchButtonStyle();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        ImGui.End();
        ImGui.PopStyleColor();
    }
    
    private void RenderGameOverPanel(Vector2 windowSize)
    {
        // Dim background
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(windowSize);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0.75f));
        
        if (ImGui.Begin("##GameOverDim", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove))
        {
            float dialogWidth = 400f;
            float dialogHeight = 280f;
            
            ImGui.SetCursorPos(new Vector2((windowSize.X - dialogWidth) / 2, (windowSize.Y - dialogHeight) / 2));
            
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeManager.PanelBg);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
            ImGui.PushStyleColor(ImGuiCol.Border, ThemeManager.PrimaryOrange);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2.0f);
            
            if (ImGui.BeginChild("##GameOverDialog", new Vector2(dialogWidth, dialogHeight), ImGuiChildFlags.Borders))
            {
                ImGui.Spacing();
                ImGui.Spacing();
                
                var result = _gameManager.LastGameResult;
                string title = result?.Winner == _gameManager.PlayerColor.ToString() ? "VICTORY" : "DEFEAT";
                if (result?.Status == GameStatus.Draw) title = "DRAW";
                
                var titleColor = title == "VICTORY" ? ThemeManager.SuccessGreen : 
                                 (title == "DEFEAT" ? ThemeManager.DangerRed : ThemeManager.WarningYellow);
                
                ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0]);
                float titleW = ImGui.CalcTextSize(title).X;
                ImGui.SetCursorPosX((dialogWidth - titleW) / 2);
                ImGui.TextColored(titleColor, title);
                ImGui.PopFont();
                
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Spacing();
                
                string reason = $"Reason: {result?.Reason.ToString() ?? "Game Over"}";
                float reasonW = ImGui.CalcTextSize(reason).X;
                ImGui.SetCursorPosX((dialogWidth - reasonW) / 2);
                ImGui.Text(reason);
                
                ImGui.Spacing();
                
                string rating = $"New Rating: {result?.NewRating} ({result?.RatingChange:+#;-#;0})";
                float ratingW = ImGui.CalcTextSize(rating).X;
                ImGui.SetCursorPosX((dialogWidth - ratingW) / 2);
                ImGui.TextColored(ThemeManager.TextHighlight, rating);
                
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();
                
                float btnWidth = 200f;
                ImGui.SetCursorPosX((dialogWidth - btnWidth) / 2);
                ThemeManager.PushOverwatchButtonStyle(true);
                if (ImGui.Button("RETURN TO LOBBY", new Vector2(btnWidth, 48)))
                {
                    _gameManager.ReturnToLobby();
                }
                ThemeManager.PopOverwatchButtonStyle();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }
        ImGui.End();
        ImGui.PopStyleColor();
    }
}
