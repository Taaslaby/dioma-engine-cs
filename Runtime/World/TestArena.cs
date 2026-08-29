// Тестовая арена: минимальный мир на чистом C# — прямоугольная комната
// с ягодами. Нужна ТОЛЬКО для тестов и headless-смоука движка: настоящая
// игра реализует мир сама (коллайдеры/тайлы Unity) и кормит существо
// готовым `Sense`, а `MotorAction` применяет своими средствами.
//
// Формулы сенсорики и движения — порт `dioma_engine::sim` (арена), чтобы
// поведение в тестах отражало поведение эталона.

using System;
using System.Collections.Generic;
using Dioma.Body;

namespace Dioma.World
{
    /// Детерминированный ГСЧ (порт dioma_engine::rng::XorShift).
    public sealed class XorShift
    {
        ulong _s;
        public XorShift(ulong seed) => _s = seed == 0 ? 0x9E3779B97F4A7C15ul : seed;

        public ulong NextU64()
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return _s;
        }

        /// [0, 1)
        public float NextFloat() => (NextU64() >> 40) / 16777216f;

        public float Range(float lo, float hi) => lo + (hi - lo) * NextFloat();
    }

    public sealed class TestArena
    {
        // ── Константы мира (эталон: sim/mod.rs) ──
        public const float SmellFalloff = 60.0f;      // strength = 60/(60+d)
        public const float VisionRange = 40.0f;
        public const float VisionHalfFov = 1.309f;    // 75°
        public const float EatRadius = 2.0f;
        public const float WallLookahead = 8.0f;
        public const float WallProbeStep = 1.0f;
        public const float WallAheadThreshold = 0.5f;
        public const float MaxTurn = 0.6f;            // рад за тик при |steer| = 1
        public const float MoveStepPerTick = 2.0f;
        public const float TickSeconds = 0.05f;
        /// Любопытство: единственный выход из аттрактора «осцилляция у стены»
        /// (при LTD-насыщении коррекционного градиента больше нет).
        public const int CuriosityAfterTicks = 600;
        public const int CuriosityPeriod = 25;
        public const float CuriositySteer = 0.6f;

        public readonly Creature Creature;
        public readonly XorShift Rng;

        /// Комната — прямоугольник [0, W] × [0, H].
        public readonly float Width, Height;
        public float X, Y, Heading;
        public readonly List<(float X, float Y)> Berries = new List<(float, float)>();

        public int Ticks { get; private set; }
        public int TicksSinceReward { get; private set; }
        public Sense LastSense { get; private set; }
        public MotorAction LastAction { get; private set; }

        bool _pendingWallHit;

        public TestArena(Creature creature, float width = 120f, float height = 120f,
                         int berries = 8, ulong seed = 7)
        {
            Creature = creature;
            Rng = new XorShift(seed);
            Width = width;
            Height = height;
            X = width * 0.5f;
            Y = height * 0.5f;
            Heading = Rng.Range(-MathF.PI, MathF.PI);
            for (int i = 0; i < berries; i++)
                Berries.Add(RandomBerry());
        }

        (float, float) RandomBerry() =>
            (Rng.Range(2f, Width - 2f), Rng.Range(2f, Height - 2f));

        bool Inside(float x, float y) => x >= 0f && x <= Width && y >= 0f && y <= Height;

        static float WrapPi(float a)
        {
            while (a > MathF.PI) a -= 2f * MathF.PI;
            while (a <= -MathF.PI) a += 2f * MathF.PI;
            return a;
        }

        /// Сенсорика мира: запах (градиент по бортам), зрение (узкий конус),
        /// пробы стен, «еда под носом».
        public Sense Observe()
        {
            var s = new Sense { WallHit = _pendingWallHit };
            _pendingWallHit = false;

            foreach (var b in Berries)
            {
                float dx = b.X - X, dy = b.Y - Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float rel = WrapPi(MathF.Atan2(dy, dx) - Heading);

                // Запах: sin-разложение — ПРЯМО ПО КУРСУ запаха почти нет.
                // Это градиент «слева/справа», а не «есть/нет еды».
                float strength = Math.Clamp(SmellFalloff / (SmellFalloff + dist), 0f, 1f);
                if (rel < 0f) s.SmellL += strength * MathF.Max(-MathF.Sin(rel), 0f);
                else s.SmellR += strength * MathF.Max(MathF.Sin(rel), 0f);

                // Зрение: дальше обоняния, но узкий конус — ловит ягоду,
                // которую «ветер несёт мимо носа».
                if (MathF.Abs(rel) <= VisionHalfFov)
                {
                    float vis = Math.Clamp(1f - dist / VisionRange, 0f, 1f);
                    if (rel < 0f) s.VisL += vis;
                    else s.VisR += vis;
                }

                if (dist <= EatRadius) s.FoodNear = true;
            }
            s.SmellL = MathF.Min(s.SmellL, 1f);
            s.SmellR = MathF.Min(s.SmellR, 1f);
            s.VisL = MathF.Min(s.VisL, 1f);
            s.VisR = MathF.Min(s.VisR, 1f);

            // Стены: лучевые пробы. Обе диагонали высоки только когда стена
            // ПРЯМО по курсу; боковая стена попадает в одну пробу.
            //
            // Сторона «L» — это отрицательный угол (то же соглашение, что у
            // запаха: rel < 0 → SmellL). Гейны анатомии замыкают контур:
            // SL→ML тянет К еде на этой стороне, WL→MR отворачивает ОТ стены
            // на ней же. Перепутать знак = ехать в стену и мимо еды.
            float pSideL = Probe(Heading - MathF.PI / 2f);
            float pSideR = Probe(Heading + MathF.PI / 2f);
            float pFwdL = Probe(Heading - MathF.PI / 4f);
            float pFwdR = Probe(Heading + MathF.PI / 4f);
            s.WallL = MathF.Max(pSideL, pFwdL);
            s.WallR = MathF.Max(pSideR, pFwdR);
            return s;
        }

        /// Проба стены в направлении `angle`: 1 − d/8 на первой точке вне
        /// комнаты, 0 если на всей длине стены нет.
        float Probe(float angle)
        {
            float cx = MathF.Cos(angle), cy = MathF.Sin(angle);
            for (float d = WallProbeStep; d <= WallLookahead; d += WallProbeStep)
            {
                if (!Inside(X + cx * d, Y + cy * d))
                    return 1f - d / WallLookahead;
            }
            return 0f;
        }

        /// Один тик мира: сенсоры → существо → движение → еда.
        public MotorAction Step()
        {
            var sense = Observe();
            var action = Creature.Tick(sense);
            LastSense = sense;
            LastAction = action;
            Ticks++;

            if (action.Asleep)
                return action;

            float steer = action.Steer;
            // Любопытство после долгого бесплодия: сбивает осцилляцию у стены.
            TicksSinceReward++;
            if (TicksSinceReward >= CuriosityAfterTicks && Ticks % CuriosityPeriod == 0)
                steer += CuriositySteer * Rng.Range(-1f, 1f);

            Heading = WrapPi(Heading + Math.Clamp(steer, -1f, 1f) * MaxTurn);
            float step = action.Drive * Creature.Blood.Speed() * MoveStepPerTick;
            float nx = X + MathF.Cos(Heading) * step;
            float ny = Y + MathF.Sin(Heading) * step;
            if (Inside(nx, ny))
            {
                X = nx;
                Y = ny;
            }
            else
            {
                // Прижались к стене: позиция клампится, боль доедет до мозга
                // следующим тиком (eligibility отворота ещё горячая).
                X = Math.Clamp(nx, 0f, Width);
                Y = Math.Clamp(ny, 0f, Height);
                _pendingWallHit = true;
            }

            // Еда: съеденная ягода перерождается в случайной точке.
            if (action.Eat)
            {
                for (int i = 0; i < Berries.Count; i++)
                {
                    float dx = Berries[i].X - X, dy = Berries[i].Y - Y;
                    if (MathF.Sqrt(dx * dx + dy * dy) <= EatRadius)
                    {
                        Berries[i] = RandomBerry();
                        TicksSinceReward = 0;
                        break;
                    }
                }
            }
            return action;
        }

        /// Дистанция до ближайшей ягоды (метрика осмысленности поведения).
        public float NearestBerryDistance()
        {
            float best = float.MaxValue;
            foreach (var b in Berries)
            {
                float dx = b.X - X, dy = b.Y - Y;
                best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy));
            }
            return best;
        }
    }
}
