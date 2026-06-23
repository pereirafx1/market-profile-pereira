// =============================================================================
//  Market Profile Pereira — Fase 1
//  Indicador ATAS SDK 10 | Modo Automático por sessão/horário
//  Fase 2 (modo Manual / drawing tool) ainda não implementada.
//
//  Marcações ⚠ VERIFICAR: pontos da API do SDK 10 que não foi possível
//  confirmar com total certeza — a lógica à volta está correcta;
//  o que pode precisar de ajuste é apenas o nome exacto do método/propriedade.
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using ATAS.Indicators;
using OFT.Rendering.Context;   // ⚠ VERIFICAR — namespace exacto em SDK 10
using OFT.Rendering.Settings;  // ⚠ VERIFICAR — namespace onde vive DrawingLayouts
using OFT.Rendering.Tools;     // RenderPen (o DrawLine do RenderContext não aceita System.Drawing.Pen)

namespace ATAS.Indicators.Custom
{
    // -------------------------------------------------------------------------
    //  Enums públicos (visíveis no editor de propriedades do ATAS)
    // -------------------------------------------------------------------------

    public enum PositionMode  { Manual, Automatico }

    public enum ProfileType   { Volume, VolumeDelta }

    public enum SessionPreset
    {
        Asia,
        London,
        NewYork,
        NYOpen,
        LondonNYOverlap,
        Daily,
        Custom
    }

    public enum CustomTZ
    {
        UTC,
        NewYork,
        London,
        Frankfurt,
        Tokyo,
        Sydney
    }

    public enum VahValModo { Linha, Zona }

    public enum ProfileLayer { PorTras, PorCima }

    // =========================================================================
    //  Classe principal
    // =========================================================================

    [DisplayName("Market Profile Pereira")]
    public class MarketProfilePereira : Indicator
    {
        // =====================================================================
        //  Tipos privados internos
        // =====================================================================

        private sealed class PriceLevelData
        {
            public decimal TotalVolume;
            public decimal BidVolume;
            public decimal AskVolume;
            public decimal Delta => AskVolume - BidVolume;
        }

        private sealed class ProfileSession
        {
            public DateTime StartTime;
            public int      StartBar;
            public int      EndBar;

            public readonly SortedDictionary<decimal, PriceLevelData> PriceLevels
                = new SortedDictionary<decimal, PriceLevelData>();

            public decimal POC;
            public decimal VAH;
            public decimal VAL;
            public readonly List<decimal> SecondaryPOCs = new List<decimal>();

            public decimal MaxVolume;
            public decimal TotalVolume;
            public bool    MetricsValid;

            // Snapshot da contribuição da última candle (para recalc no tick ao vivo)
            public Dictionary<decimal, PriceLevelData> LastBarContrib;
            public int LastBarAdded = -1;
        }

        // =====================================================================
        //  Propriedades / Settings
        // =====================================================================

        // --- Profile ---

        [Display(Name = "Position Mode",
                 Description = "Automatico = sessão por horário. Manual = Fase 2 (ainda não implementado).",
                 GroupName = "Profile", Order = 0)]
        public PositionMode PositionMode { get; set; } = PositionMode.Automatico;

        [Display(Name = "Profile Type",
                 Description = "Volume: barras de volume total. VolumeDelta: volume (direita) + delta bid/ask (esquerda).",
                 GroupName = "Profile", Order = 1)]
        public ProfileType ProfileType
        {
            get => _profileType;
            set { _profileType = value; RecalculateValues(); }
        }

        [Display(Name = "Largura máxima (% do range da sessão)",
                 Description = "Percentagem do range X da sessão que as barras do perfil podem ocupar.",
                 GroupName = "Profile", Order = 2)]
        public int MaxWidthPercent { get; set; } = 70;

        [Display(Name = "Layer",
                 Description = "PorTras = profile desenhado atrás das candles. PorCima = por cima de tudo.",
                 GroupName = "Profile", Order = 3)]
        public ProfileLayer ProfileLayer
        {
            get => _profileLayer;
            set
            {
                _profileLayer        = value;
                DrawAbovePrice       = (value == ProfileLayer.PorCima);
            }
        }

        [Display(Name = "Opacidade do profile (0–100)",
                 Description = "Aplica-se a todas as barras, linhas e zonas do profile.",
                 GroupName = "Profile", Order = 4)]
        public int ProfileOpacity { get; set; } = 80;

        // --- Sessions ---

        [Display(Name = "Sessão",
                 GroupName = "Sessions", Order = 10)]
        public SessionPreset Session
        {
            get => _session;
            set { _session = value; RecalculateValues(); }
        }

        [Display(Name = "Custom — Timezone",
                 Description = "Timezone das horas Custom Start / Custom End.",
                 GroupName = "Sessions", Order = 11)]
        public CustomTZ CustomTimezone
        {
            get => _customTimezone;
            set { _customTimezone = value; RecalculateValues(); }
        }

        [Display(Name = "Custom Start (HH:mm)",
                 Description = "Hora de início da sessão custom. Formato HH:mm, no timezone acima.",
                 GroupName = "Sessions", Order = 12)]
        public string CustomStart
        {
            get => _customStart;
            set { _customStart = value; RecalculateValues(); }
        }

        [Display(Name = "Custom End (HH:mm)",
                 Description = "Hora de fim da sessão custom. Formato HH:mm, no timezone acima.",
                 GroupName = "Sessions", Order = 13)]
        public string CustomEnd
        {
            get => _customEnd;
            set { _customEnd = value; RecalculateValues(); }
        }

        [Display(Name = "Sessões a mostrar",
                 Description = "Número de sessões históricas a desenhar (+ a sessão actual).",
                 GroupName = "Sessions", Order = 14)]
        public int SessionsToShow
        {
            get => _sessionsToShow;
            set { _sessionsToShow = value; RecalculateValues(); }
        }

        // --- Value Area ---

        // "ValueAreaPercent" já existe na classe base Indicator — usar nome diferente
        [Display(Name = "Value Area %",
                 GroupName = "Value Area", Order = 20)]
        public decimal VAPercent
        {
            get => _vaPercent;
            set { _vaPercent = value; RecalculateValues(); }
        }

        [Display(Name = "VAH / VAL modo",
                 Description = "Linha = linha horizontal tracejada. Zona = rectângulo semi-transparente.",
                 GroupName = "Value Area", Order = 21)]
        public VahValModo VahValMode { get; set; } = VahValModo.Linha;

        [Display(Name = "VAH — cor",
                 GroupName = "Value Area", Order = 22)]
        public Color CorVAH { get; set; } = Color.FromArgb(255, 220, 50, 50);

        [Display(Name = "VAL — cor",
                 GroupName = "Value Area", Order = 23)]
        public Color CorVAL { get; set; } = Color.FromArgb(255, 50, 180, 220);

        [Display(Name = "Zona — opacidade (0-255)",
                 Description = "Alpha do preenchimento quando VAH/VAL estão em modo Zona.",
                 GroupName = "Value Area", Order = 24)]
        public int ZoneAlpha { get; set; } = 40;

        // --- POC ---

        [Display(Name = "POC — cor",
                 GroupName = "POC", Order = 30)]
        public Color CorPOC { get; set; } = Color.FromArgb(255, 255, 165, 0);

        [Display(Name = "Mostrar POCs secundários",
                 Description = "Activar/desactivar a marcação de POCs secundários.",
                 GroupName = "POC", Order = 31)]
        public bool MostrarPOCsSecundarios { get; set; } = true;

        [Display(Name = "POCs secundários (máximo)",
                 Description = "Quantos POCs secundários mostrar. 0 = desactivado.",
                 GroupName = "POC", Order = 32)]
        public int MaxSecondaryPOCs { get; set; } = 3;

        [Display(Name = "POC secundário — volume mínimo (%)",
                 Description = "Um pico só é candidato a POC secundário se tiver pelo menos X% do volume do POC principal.",
                 GroupName = "POC", Order = 33)]
        public int SecondaryPOCThresholdPct { get; set; } = 60;

        [Display(Name = "POC secundário — cor",
                 GroupName = "POC", Order = 34)]
        public Color CorPOCSecundario { get; set; } = Color.FromArgb(255, 180, 80, 255);

        // --- Colors — Volume ---

        [Display(Name = "Volume dentro da Value Area",
                 GroupName = "Colors — Volume", Order = 40)]
        public Color CorVolumePerfil { get; set; } = Color.FromArgb(200, 255, 100, 0);

        [Display(Name = "Volume fora da Value Area",
                 GroupName = "Colors — Volume", Order = 41)]
        public Color CorForaValueArea { get; set; } = Color.FromArgb(200, 255, 220, 0);

        // --- Colors — Delta ---

        [Display(Name = "Delta negativo (dominância Bid)",
                 GroupName = "Colors — Delta", Order = 50)]
        public Color CorBid { get; set; } = Color.FromArgb(200, 130, 0, 210);

        [Display(Name = "Delta positivo (dominância Ask)",
                 GroupName = "Colors — Delta", Order = 51)]
        public Color CorAsk { get; set; } = Color.FromArgb(200, 0, 200, 60);

        // =====================================================================
        //  Estado interno
        // =====================================================================

        // Backing fields para propriedades que forçam RecalculateValues()
        private ProfileLayer   _profileLayer   = ProfileLayer.PorTras;
        private ProfileType    _profileType    = ProfileType.Volume;
        private SessionPreset  _session        = SessionPreset.NewYork;
        private CustomTZ       _customTimezone = CustomTZ.NewYork;
        private string         _customStart    = "09:30";
        private string         _customEnd      = "16:00";
        private int            _sessionsToShow = 3;
        private decimal        _vaPercent      = 70m;

        private readonly List<ProfileSession> _sessions     = new List<ProfileSession>();
        private          ProfileSession       _currentSession;

        // =====================================================================
        //  Construtor
        // =====================================================================

        public MarketProfilePereira()
        {
            EnableCustomDrawing = true;
            DenyToChangePanel   = true;
            DrawAbovePrice      = false; // Tentativa 1: propriedade da classe base para layer acima das candles
        }

        // =====================================================================
        //  Lifecycle
        // =====================================================================

        protected override void OnInitialize()
        {
            _sessions.Clear();
            _currentSession = null;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Reset completo na primeira candle (inclui re-cálculo por mudança de settings)
            if (bar == 0)
            {
                _sessions.Clear();
                _currentSession = null;
            }

            var candle = GetCandle(bar);
            if (candle == null) return;

            DateTime t      = candle.Time;
            bool     inSess = IsInCurrentSession(t);
            bool     isLive = (bar == CurrentBar - 1);

            if (inSess)
            {
                DateTime anchor = GetCurrentSessionAnchor(t);

                bool needNew = _currentSession == null
                    || GetCurrentSessionAnchor(_currentSession.StartTime) != anchor;

                if (needNew)
                {
                    ArchiveCurrentSession();
                    _currentSession = new ProfileSession
                    {
                        StartTime    = t,
                        StartBar     = bar,
                        EndBar       = bar,
                        LastBarAdded = -1
                    };
                }

                _currentSession.EndBar = bar;

                if (bar > _currentSession.LastBarAdded)
                {
                    // Nova candle fechada: acumular de forma incremental
                    AddBarToSession(_currentSession, bar);
                    _currentSession.LastBarAdded = bar;
                    _currentSession.MetricsValid = false;
                }
                else if (isLive)
                {
                    // Tick ao vivo na candle actual: retirar contribuição anterior e re-adicionar
                    SubtractContrib(_currentSession, _currentSession.LastBarContrib);
                    AddBarToSession(_currentSession, bar);
                    _currentSession.MetricsValid = false;
                }

                // Manter métricas actualizadas para o render
                if (isLive && !_currentSession.MetricsValid)
                    ComputeMetrics(_currentSession);
            }
            else
            {
                // Esta candle está fora da sessão — fechar sessão actual se existir
                ArchiveCurrentSession();
                _currentSession = null;
            }
        }

        // =====================================================================
        //  Gestão de sessões
        // =====================================================================

        private void ArchiveCurrentSession()
        {
            if (_currentSession == null) return;
            if (!_currentSession.MetricsValid)
                ComputeMetrics(_currentSession);
            if (_currentSession.PriceLevels.Count > 0)
            {
                _sessions.Add(_currentSession);
                // Manter apenas as N mais recentes
                int max = Math.Max(1, SessionsToShow);
                while (_sessions.Count > max)
                    _sessions.RemoveAt(0);
            }
            _currentSession = null;
        }

        private static TimeZoneInfo GetCustomTzInfo(CustomTZ tz)
        {
            switch (tz)
            {
                case CustomTZ.NewYork:    return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                case CustomTZ.London:     return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                case CustomTZ.Frankfurt:  return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
                case CustomTZ.Tokyo:      return TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                case CustomTZ.Sydney:     return TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");
                default:                  return TimeZoneInfo.Utc;
            }
        }

        // Converte um timestamp UTC para o timezone do Custom e devolve o DateTime local.
        private DateTime ToCustomLocalTime(DateTime utcTime)
            => TimeZoneInfo.ConvertTime(utcTime, GetCustomTzInfo(CustomTimezone));

        // Devolve true se o timestamp UTC da candle pertence à sessão configurada.
        private bool IsInCurrentSession(DateTime utcTime)
        {
            if (Session == SessionPreset.Daily) return true;

            if (Session == SessionPreset.Custom)
            {
                DateTime local   = ToCustomLocalTime(utcTime);
                var (cs, ce)     = ParseCustomTimes();
                return IsInSessionSpan(local.TimeOfDay, cs, ce);
            }

            var (s, e) = GetPresetSessionTimesUtc();
            return IsInSessionSpan(utcTime.TimeOfDay, s, e);
        }

        // Devolve a data âncora que agrupa todas as candles do mesmo dia de sessão.
        private DateTime GetCurrentSessionAnchor(DateTime utcTime)
        {
            if (Session == SessionPreset.Daily) return utcTime.Date;

            if (Session == SessionPreset.Custom)
            {
                DateTime local = ToCustomLocalTime(utcTime);
                var (cs, ce)   = ParseCustomTimes();
                if (cs > ce && local.TimeOfDay < ce)
                    return local.Date.AddDays(-1);
                return local.Date;
            }

            var (s, e) = GetPresetSessionTimesUtc();
            if (s > e && utcTime.TimeOfDay < e)
                return utcTime.Date.AddDays(-1);
            return utcTime.Date;
        }

        private (TimeSpan start, TimeSpan end) ParseCustomTimes()
        {
            if (TimeSpan.TryParse(CustomStart, out var cs) && TimeSpan.TryParse(CustomEnd, out var ce))
                return (cs, ce);
            return (new TimeSpan(9, 30, 0), new TimeSpan(16, 0, 0));
        }

        private (TimeSpan start, TimeSpan end) GetPresetSessionTimesUtc()
        {
            switch (Session)
            {
                case SessionPreset.Asia:            return (new TimeSpan(0,  0, 0), new TimeSpan(9,  0, 0));
                case SessionPreset.London:          return (new TimeSpan(7,  0, 0), new TimeSpan(16, 0, 0));
                case SessionPreset.NewYork:         return (new TimeSpan(13, 0, 0), new TimeSpan(22, 0, 0));
                case SessionPreset.NYOpen:          return (new TimeSpan(13,30, 0), new TimeSpan(15, 0, 0));
                case SessionPreset.LondonNYOverlap: return (new TimeSpan(12, 0, 0), new TimeSpan(16,30, 0));
                default:                            return (new TimeSpan(13, 0, 0), new TimeSpan(22, 0, 0));
            }
        }

        private static bool IsInSessionSpan(TimeSpan t, TimeSpan start, TimeSpan end)
        {
            if (start <= end) return t >= start && t < end;
            return t >= start || t < end; // atravessa meia-noite
        }

        // =====================================================================
        //  Cálculo do perfil (acumulação incremental)
        // =====================================================================

        private void AddBarToSession(ProfileSession session, int bar)
        {
            var candle = GetCandle(bar);
            if (candle == null) return;

            decimal tick = InstrumentInfo.TickSize;
            if (tick <= 0) tick = 0.01m;

            var contrib = new Dictionary<decimal, PriceLevelData>();

            for (decimal price = candle.Low; price <= candle.High + tick * 0.001m; price += tick)
            {
                decimal p   = RoundToTick(price, tick);
                var     pvi = candle.GetPriceVolumeInfo(p);
                if (pvi == null) continue;

                decimal ask = Math.Max(0m, pvi.Ask);
                decimal bid = Math.Max(0m, pvi.Bid);
                decimal tot = ask + bid;
                if (tot <= 0) continue;

                AccumulateLevel(session, contrib, p, ask, bid, tot);
            }

            session.LastBarContrib = contrib;
        }

        private static void AccumulateLevel(ProfileSession session,
                                            Dictionary<decimal, PriceLevelData> contrib,
                                            decimal price, decimal ask, decimal bid, decimal total)
        {
            if (!session.PriceLevels.TryGetValue(price, out var lvl))
            { lvl = new PriceLevelData(); session.PriceLevels[price] = lvl; }
            lvl.AskVolume   += ask;
            lvl.BidVolume   += bid;
            lvl.TotalVolume += total;

            if (!contrib.TryGetValue(price, out var snap))
            { snap = new PriceLevelData(); contrib[price] = snap; }
            snap.AskVolume   += ask;
            snap.BidVolume   += bid;
            snap.TotalVolume += total;
        }

        private static void SubtractContrib(
            ProfileSession session,
            Dictionary<decimal, PriceLevelData> contrib)
        {
            if (contrib == null) return;
            foreach (var kvp in contrib)
            {
                if (!session.PriceLevels.TryGetValue(kvp.Key, out var lvl)) continue;
                lvl.AskVolume   -= kvp.Value.AskVolume;
                lvl.BidVolume   -= kvp.Value.BidVolume;
                lvl.TotalVolume -= kvp.Value.TotalVolume;
                if (lvl.TotalVolume <= 0)
                    session.PriceLevels.Remove(kvp.Key);
            }
        }

        // =====================================================================
        //  Métricas: POC, Value Area, POCs secundários
        // =====================================================================

        private void ComputeMetrics(ProfileSession session)
        {
            session.MetricsValid = true;
            if (session.PriceLevels.Count == 0) return;

            // POC e volume total
            decimal maxVol = 0m, totVol = 0m, poc = 0m;
            foreach (var kvp in session.PriceLevels)
            {
                totVol += kvp.Value.TotalVolume;
                if (kvp.Value.TotalVolume > maxVol)
                {
                    maxVol = kvp.Value.TotalVolume;
                    poc    = kvp.Key;
                }
            }
            session.MaxVolume   = maxVol;
            session.TotalVolume = totVol;
            session.POC         = poc;

            ComputeValueArea(session);
            FindSecondaryPOCs(session);
        }

        private void ComputeValueArea(ProfileSession session)
        {
            // Defaulta ao POC se não houver dados suficientes
            session.VAL = session.POC;
            session.VAH = session.POC;

            var levels = session.PriceLevels.ToList(); // ascendente por preço
            int n = levels.Count;
            if (n == 0) return;

            int pocIdx = levels.FindIndex(kvp => kvp.Key == session.POC);
            if (pocIdx < 0) return;

            decimal target      = session.TotalVolume * (VAPercent / 100m);
            decimal accumulated = levels[pocIdx].Value.TotalVolume;
            int lo = pocIdx, hi = pocIdx;

            while (accumulated < target)
            {
                decimal below = (lo > 0)     ? levels[lo - 1].Value.TotalVolume : -1m;
                decimal above = (hi < n - 1) ? levels[hi + 1].Value.TotalVolume : -1m;

                if (below < 0 && above < 0) break;

                // Expandir para o lado com mais volume (método standard CME/TPO)
                if (above >= below)
                    accumulated += levels[++hi].Value.TotalVolume;
                else
                    accumulated += levels[--lo].Value.TotalVolume;
            }

            session.VAL = levels[lo].Key;
            session.VAH = levels[hi].Key;
        }

        private void FindSecondaryPOCs(ProfileSession session)
        {
            session.SecondaryPOCs.Clear();
            if (!MostrarPOCsSecundarios || MaxSecondaryPOCs <= 0) return;

            var levels = session.PriceLevels.ToList(); // ascendente por preço
            int n = levels.Count;
            if (n < 3) return;

            int     pocIdx    = levels.FindIndex(kvp => kvp.Key == session.POC);
            decimal threshold = session.MaxVolume * (SecondaryPOCThresholdPct / 100m);

            // 1. Encontrar todos os picos locais acima do limiar (excluindo o POC principal)
            var candidates = new List<(int idx, decimal price, decimal vol)>();
            for (int i = 1; i < n - 1; i++)
            {
                if (i == pocIdx) continue;
                decimal vol = levels[i].Value.TotalVolume;
                if (vol < threshold) continue;
                if (vol > levels[i - 1].Value.TotalVolume && vol > levels[i + 1].Value.TotalVolume)
                    candidates.Add((i, levels[i].Key, vol));
            }
            if (candidates.Count == 0) return;

            // 2. Ordenar por volume descendente — os picos mais proeminentes primeiro
            candidates.Sort((a, b) => b.vol.CompareTo(a.vol));

            // 3. Selecção greedy: aceitar pico só se estiver separado de TODOS os já aceites
            //    por uma LVA significativa (valley < 50 % do menor dos dois picos).
            //    Garante que cada POC secundário pertence a uma zona de alto volume distinta.
            var accepted = new List<int> { pocIdx }; // começa com o POC principal como referência

            foreach (var (candIdx, candPrice, _) in candidates)
            {
                if (session.SecondaryPOCs.Count >= MaxSecondaryPOCs) break;

                bool ok = true;
                foreach (int accIdx in accepted)
                {
                    int lo = Math.Min(candIdx, accIdx);
                    int hi = Math.Max(candIdx, accIdx);
                    if (hi - lo <= 1) { ok = false; break; } // adjacentes — sem LVA entre eles

                    // Volume mínimo no corredor entre os dois picos
                    decimal valleyMin = decimal.MaxValue;
                    for (int j = lo + 1; j < hi; j++)
                        valleyMin = Math.Min(valleyMin, levels[j].Value.TotalVolume);

                    // LVA válida: valley abaixo de 50 % do menor dos dois picos
                    decimal lowerPeak = Math.Min(levels[candIdx].Value.TotalVolume,
                                                 levels[accIdx].Value.TotalVolume);
                    if (valleyMin >= lowerPeak * 0.50m) { ok = false; break; }
                }

                if (ok)
                {
                    session.SecondaryPOCs.Add(candPrice);
                    accepted.Add(candIdx);
                }
            }
        }

        private static decimal RoundToTick(decimal price, decimal tick)
            => Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;

        // =====================================================================
        //  Rendering
        // =====================================================================

        // Aplica a opacidade global (0-100) à componente alpha de uma cor.
        private Color ApplyOpacity(Color c)
        {
            int pct   = Math.Max(0, Math.Min(100, ProfileOpacity));
            int alpha = (int)Math.Round(c.A * pct / 100.0);
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // Modo Manual reservado para Fase 2 — não desenha nada por agora
            if (PositionMode == PositionMode.Manual) return;

            // PorTras: só desenha nos layers de fundo (exclui Final que nunca é chamado pelo ATAS).
            // PorCima: sem filtro — desenha em todos os passes; o último compose fica sobre as candles.
            if (ProfileLayer == ProfileLayer.PorTras && layout == DrawingLayouts.Final) return;

            // Colecionar sessões a renderizar (históricas + actual)
            var toRender = new List<ProfileSession>(_sessions);
            if (_currentSession != null && _currentSession.PriceLevels.Count > 0)
            {
                // Forçar cálculo de métricas se ainda não foi feito
                if (!_currentSession.MetricsValid)
                    ComputeMetrics(_currentSession);
                toRender.Add(_currentSession);
            }

            foreach (var s in toRender)
            {
                if (!s.MetricsValid || s.PriceLevels.Count == 0) continue;
                DrawSession(context, s);
            }
        }

        // ---------------------------------------------------------------------
        //  Desenho de uma sessão completa
        // ---------------------------------------------------------------------

        private void DrawSession(RenderContext context, ProfileSession session)
        {
            decimal tick = InstrumentInfo.TickSize;
            if (tick <= 0) tick = 0.01m;

            var region    = ChartInfo.PriceChartContainer.Region;
            int viewLeft  = region.Left;
            int viewRight = region.Right;
            if (viewRight <= viewLeft) return;

            // GetBarX devolve 0 para barras fora do ecrã (qualquer lado).
            // Em vez de estimar firstVisible (que falha quando as barras são sub-pixel),
            // determinamos x1/x2 directamente a partir dos X reais dos extremos da sessão.
            int xStart = GetBarX(session.StartBar);
            int xEnd   = GetBarX(session.EndBar);

            int x1, x2;

            if (xStart != 0 && xEnd != 0)
            {
                // Ambos os extremos estão no ecrã — posições exactas.
                x1 = Math.Min(xStart, xEnd);
                x2 = Math.Max(xStart, xEnd);
            }
            else if (xStart == 0 && xEnd != 0)
            {
                // Início fora do ecrã à esquerda; fim visível — cortar à borda esquerda.
                x1 = viewLeft;
                x2 = xEnd;
            }
            else if (xStart != 0)   // xEnd == 0
            {
                // Início visível; fim fora do ecrã à direita — cortar à borda direita.
                x1 = xStart;
                x2 = viewRight;
            }
            else
            {
                // Ambos fora do ecrã. Dois casos possíveis:
                //   A) Sessão abrange o viewport (StartBar fora à esq., EndBar fora à dir.) → largura total.
                //   B) Sessão completamente fora do viewport → não desenhar.
                // Um único ponto central falha quando o viewport está na metade posterior da sessão
                // (o ponto central fica fora à esquerda e GetBarX devolve 0 incorrectamente).
                // Solução: amostrar 15 pontos igualmente espaçados — qualquer janela > 1/16 da
                // duração da sessão garante que pelo menos um ponto cai dentro do viewport.
                int range = session.EndBar - session.StartBar;
                if (range <= 0) return;
                bool found = false;
                for (int k = 1; k <= 15 && !found; k++)
                    found = GetBarX(session.StartBar + (int)((long)range * k / 16)) != 0;
                if (!found) return;
                x1 = viewLeft;
                x2 = viewRight;
            }

            x1 = Math.Max(x1, viewLeft);
            x2 = Math.Min(x2, viewRight);
            if (x1 >= x2) return;

            int totalWidth  = Math.Max(2, x2 - x1);
            int profileMaxW = Math.Max(2, totalWidth * MaxWidthPercent / 100);

            // x-range para linhas de nível (POC, VAH, VAL):
            //   Volume       → cobre a largura das barras (x1 … x1+profileMaxW)
            //   Volume+Delta → apenas lado direito / volume (centerX … x2)
            int levelX1, levelX2;
            if (ProfileType == ProfileType.Volume)
            {
                DrawVolumeProfile(context, session, x1, profileMaxW, tick);
                levelX1 = x1;
                levelX2 = x1 + profileMaxW;
            }
            else
            {
                int centerX = x1 + totalWidth / 2;
                DrawVolumeDeltaProfile(context, session, x1, x2, profileMaxW, tick);
                levelX1 = centerX;
                levelX2 = x2;
            }

            DrawKeyLevels(context, session, levelX1, levelX2, tick);
        }

        // ---------------------------------------------------------------------
        //  Modo Volume — barras de volume total, ancoradas à esquerda da sessão
        // ---------------------------------------------------------------------

        private void DrawVolumeProfile(RenderContext context, ProfileSession session,
                                       int x1, int maxW, decimal tick)
        {
            if (session.MaxVolume <= 0) return;

            foreach (var kvp in session.PriceLevels)
            {
                decimal vol = kvp.Value.TotalVolume;
                if (vol <= 0) continue;

                int barW = Math.Max(1, (int)(vol / session.MaxVolume * maxW));

                GetLevelRect(kvp.Key, tick, out int yTop, out int barH);

                bool  inVA = kvp.Key >= session.VAL && kvp.Key <= session.VAH;
                Color col  = ApplyOpacity(inVA ? CorVolumePerfil : CorForaValueArea);

                context.FillRectangle(col, new Rectangle(x1, yTop, barW, barH));
            }
        }

        // ---------------------------------------------------------------------
        //  Modo Volume + Delta
        //  Eixo central: volume à direita, delta bid/ask à esquerda
        // ---------------------------------------------------------------------

        private void DrawVolumeDeltaProfile(RenderContext context, ProfileSession session,
                                            int x1, int x2, int maxW, decimal tick)
        {
            if (session.MaxVolume <= 0) return;

            int halfW   = Math.Max(2, maxW / 2);
            int totalW  = x2 - x1;
            int centerX = x1 + totalW / 2;

            // Escalar o lado delta pelo máximo de |delta| entre todos os níveis
            decimal maxAbsDelta = 1m;
            foreach (var lvl in session.PriceLevels.Values)
            {
                decimal ad = Math.Abs(lvl.Delta);
                if (ad > maxAbsDelta) maxAbsDelta = ad;
            }

            foreach (var kvp in session.PriceLevels)
            {
                var data = kvp.Value;
                if (data.TotalVolume <= 0) continue;

                GetLevelRect(kvp.Key, tick, out int yTop, out int barH);

                // Lado direito: volume total
                int volW = Math.Max(1, (int)(data.TotalVolume / session.MaxVolume * halfW));
                bool inVA = kvp.Key >= session.VAL && kvp.Key <= session.VAH;
                context.FillRectangle(
                    ApplyOpacity(inVA ? CorVolumePerfil : CorForaValueArea),
                    new Rectangle(centerX, yTop, volW, barH));

                // Lado esquerdo: |delta|, cor indica dominância
                int   deltaW = Math.Max(1, (int)(Math.Abs(data.Delta) / maxAbsDelta * halfW));
                Color deltaC = ApplyOpacity(data.Delta >= 0 ? CorAsk : CorBid);
                context.FillRectangle(deltaC,
                    new Rectangle(centerX - deltaW, yTop, deltaW, barH));
            }
        }

        // ---------------------------------------------------------------------
        //  Linhas de nível: POC, VAH, VAL, POCs secundários
        // ---------------------------------------------------------------------

        private void DrawKeyLevels(RenderContext context, ProfileSession session,
                                   int x1, int x2, decimal tick)
        {
            // VAH
            DrawHorizontalLevel(context, session.VAH, x1, x2, tick, CorVAH);
            // VAL
            DrawHorizontalLevel(context, session.VAL, x1, x2, tick, CorVAL);

            // POC principal — rect 2px de altura
            int pocY = PriceToY(session.POC + tick * 0.5m);
            context.FillRectangle(ApplyOpacity(CorPOC), new Rectangle(x1, pocY - 1, x2 - x1, 2));

            // POCs secundários — linha tracejada fina
            if (MostrarPOCsSecundarios && session.SecondaryPOCs.Count > 0)
            {
                var dpen = new RenderPen(ApplyOpacity(CorPOCSecundario), 1f) { DashStyle = DashStyle.Dash };
                foreach (decimal sp in session.SecondaryPOCs)
                {
                    int sy = PriceToY(sp + tick * 0.5m);
                    context.DrawLine(dpen, x1, sy, x2, sy);
                }
            }
        }

        private void DrawHorizontalLevel(RenderContext context, decimal price,
                                         int x1, int x2, decimal tick, Color color)
        {
            if (VahValMode == VahValModo.Linha)
            {
                int y         = PriceToY(price + tick * 0.5m);
                var lineColor = ApplyOpacity(Color.FromArgb(220, color.R, color.G, color.B));
                context.FillRectangle(lineColor, new Rectangle(x1, y, x2 - x1, 1));
            }
            else // Zona
            {
                int yTop  = PriceToY(price + tick);
                int yBot  = PriceToY(price);
                int zoneH = Math.Max(1, Math.Abs(yBot - yTop));
                int yDraw = Math.Min(yTop, yBot);

                int alpha  = Math.Max(0, Math.Min(255, ZoneAlpha));
                var zColor = ApplyOpacity(Color.FromArgb(alpha, color.R, color.G, color.B));
                context.FillRectangle(zColor, new Rectangle(x1, yDraw, x2 - x1, zoneH));
            }
        }

        // =====================================================================
        //  Helpers de coordenadas
        //  ⚠ VERIFICAR: ajustar os nomes dos métodos do ChartInfo para os exactos
        //  do SDK 10. São aqui centralizados para facilitar a correcção.
        // =====================================================================

        // Converte índice de candle → coordenada X em pixels
        private int GetBarX(int barIndex)
        {
            // IChartContainer (PriceChartContainer) não tem GetXCoordinate.
            // Por simetria com GetYByPrice, tentar GetXByBar.
            // ⚠ VERIFICAR nome exacto do método em IChartContainer no SDK 10:
            //   .GetXByBar(barIndex)
            //   .GetX(barIndex)
            //   .GetXCoordinate(barIndex)
            return (int)ChartInfo.PriceChartContainer.GetXByBar(barIndex); // ⚠ VERIFICAR
        }

        // Converte preço → coordenada Y em pixels.
        // Em ATAS, Y cresce para baixo; preços maiores têm Y menor (mais acima no ecrã).
        private int PriceToY(decimal price)
        {
            // IChartContainer.GetYByPrice confirmado por CS7036 — precisa de 2 parâmetros:
            //   (decimal price, bool isStartOfPriceLevel)
            // false = Y do meio/topo do price level (usar para o traço do indicador)
            return (int)ChartInfo.PriceChartContainer.GetYByPrice(price, false);
        }

        // Calcula o rectângulo (em pixels) de um price level [price, price+tick[
        private void GetLevelRect(decimal price, decimal tick, out int yTop, out int barH)
        {
            int yA = PriceToY(price + tick);
            int yB = PriceToY(price);
            yTop = Math.Min(yA, yB);
            barH = Math.Max(1, Math.Abs(yB - yA));
        }
    }
}
