// Тестовая арена для существ по спецификации вида: тот же мирок, что
// TestArena, но каналы заполняются по ИМЕНАМ вида, а не по фиксированным
// полям Sense.
//
// Мир знает про геометрию (стены, еда, курс) и про модальности («запах к
// цели», «луч до стены», «еда в радиусе укуса»). Какие именно каналы у вида
// есть — он спрашивает у спецификации: нет канала «зрение» → мир его и не
// считает. Формулы общие с TestArena (SenseMath), чтобы поведение старого и
// нового пути совпадало.

using System;
using System.Collections.Generic;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;

namespace Dioma.World
{
    public sealed class SpeciesArena
    {
        // Геометрия и движение — как в TestArena.
        public const float MaxTurn = 0.6f;
        public const float MoveStepPerTick = 2.0f;
        public const float TickSeconds = 0.05f;
        public const int CuriosityAfterTicks = 600;
        public const int CuriosityPeriod = 25;
        public const float CuriositySteer = 0.6f;

        public readonly SpeciesCreature Creature;
        public readonly XorShift Rng;
        public readonly float Width, Height;
        public float X, Y, Heading;
        public readonly List<(float X, float Y)> Berries = new List<(float, float)>();

        public int Ticks { get; private set; }
        public int TicksSinceReward { get; private set; }

        readonly SenseFrame _sense;
        readonly int _smellL, _smellR, _visL, _visR, _wallL, _wallR, _foodNear;
        readonly int _steer, _drive, _eat;
        bool _pendingBlocked;

        public SenseFrame LastSense => _sense;
        public ActionFrame LastAction => Creature.LastAction;

        public SpeciesArena(SpeciesCreature creature, float width = 80f, float height = 16f,
                            int berries = 8, ulong seed = 7)
        {
            Creature = creature ?? throw new ArgumentNullException(nameof(creature));
            Rng = new XorShift(seed);
            Width = width;
            Height = height;
            X = width * 0.5f;
            Y = height * 0.5f;
            // Порядок потребления RNG тот же, что в TestArena: сначала курс,
            // потом ягоды. Иначе два мира разойдутся с первого тика.
            Heading = Rng.Range(-MathF.PI, MathF.PI);
            for (int i = 0; i < berries; i++)
                Berries.Add(RandomBerry());

            var sense = creature.Species.Sense;
            _sense = new SenseFrame(sense);
            // Индексы каналов разрешаются ОДИН раз: в тике только массивы.
            // −1 значит «у вида нет такой модальности» — мир её не считает.
            _smellL = sense.IndexOf(Species.SmellLeft);
            _smellR = sense.IndexOf(Species.SmellRight);
            _visL = sense.IndexOf(Species.VisionLeft);
            _visR = sense.IndexOf(Species.VisionRight);
            _wallL = sense.IndexOf(Species.WallLeft);
            _wallR = sense.IndexOf(Species.WallRight);
            _foodNear = sense.IndexOf(Species.FoodNear);

            var action = creature.Species.Action;
            _steer = action.IndexOf(Species.Steer);
            _drive = action.IndexOf(Species.Drive);
            _eat = action.IndexOf(Species.Eat);
        }

        /// Заполнить кадр ощущений: еда и стены вокруг существа.
        public SenseFrame Observe()
        {
            _sense.Clear();
            _sense.Blocked = _pendingBlocked;
            _pendingBlocked = false;

            // Накапливаем в ЛОКАЛЬНЫЕ float теми же выражениями, что
            // TestArena. Через границу вызова нельзя: `acc += a * b` компилятор
            // вправе посчитать с расширенной точностью промежутка, а передача
            // произведения аргументом округляет его до float — расхождение в
            // один ulp, которое в хаотичной сети за сотню тиков становится
            // другой траекторией.
            float smellL = 0f, smellR = 0f, visL = 0f, visR = 0f;
            bool foodNear = false;

            foreach (var (bx, by) in Berries)
            {
                float dx = bx - X, dy = by - Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float rel = SenseMath.WrapPi(MathF.Atan2(dy, dx) - Heading);

                // Запах — ГРАДИЕНТ: множитель sin даёт нуль прямо по курсу,
                // существо различает сторону, а не наличие еды.
                float strength = Math.Clamp(
                    SenseMath.SmellFalloff / (SenseMath.SmellFalloff + dist), 0f, 1f);
                if (rel < 0f) smellL += strength * MathF.Max(-MathF.Sin(rel), 0f);
                else smellR += strength * MathF.Max(MathF.Sin(rel), 0f);

                if (MathF.Abs(rel) <= SenseMath.VisionHalfFov)
                {
                    float vis = Math.Clamp(1f - dist / SenseMath.VisionRange, 0f, 1f);
                    if (rel < 0f) visL += vis;
                    else visR += vis;
                }

                if (dist <= SenseMath.EatRadius) foodNear = true;
            }

            // Каналы насыщаются: несколько ягод не дают тока больше одной.
            Set(_smellL, MathF.Min(smellL, 1f));
            Set(_smellR, MathF.Min(smellR, 1f));
            Set(_visL, MathF.Min(visL, 1f));
            Set(_visR, MathF.Min(visR, 1f));
            if (foodNear) Set(_foodNear, 1f);

            // Стены: пробы по бортам и вперёд-вбок. Обе диагонали высоки
            // только когда стена ПРЯМО по курсу; боковая попадает в одну.
            if (_wallL >= 0)
                Set(_wallL, MathF.Max(Probe(Heading - MathF.PI / 2f),
                                      Probe(Heading - MathF.PI / 4f)));
            if (_wallR >= 0)
                Set(_wallR, MathF.Max(Probe(Heading + MathF.PI / 2f),
                                      Probe(Heading + MathF.PI / 4f)));
            return _sense;
        }

        void Set(int channel, float value) { if (channel >= 0) _sense.Values[channel] = value; }

        (float, float) RandomBerry() =>
            (Rng.Range(2f, Width - 2f), Rng.Range(2f, Height - 2f));

        /// Близость стены по направлению: шагаем лучом до предела ощущения.
        float Probe(float angle)
        {
            float cx = MathF.Cos(angle), cy = MathF.Sin(angle);
            for (float d = SenseMath.WallProbeStep; d <= SenseMath.WallLookahead;
                 d += SenseMath.WallProbeStep)
            {
                if (!Inside(X + cx * d, Y + cy * d))
                    return SenseMath.WallProximity(d);
            }
            return 0f;
        }

        bool Inside(float x, float y) => x >= 0f && x <= Width && y >= 0f && y <= Height;

        /// Один тик мира.
        public ActionFrame Step()
        {
            var sense = Observe();
            var action = Creature.Tick(sense);
            Ticks++;
            if (action.Asleep)
                return action;

            // Любопытство: без награды существо начинает пробовать.
            TicksSinceReward++;
            float steer = _steer >= 0 ? action.Values[_steer] : 0f;
            if (TicksSinceReward >= CuriosityAfterTicks && Ticks % CuriosityPeriod == 0)
                steer += CuriositySteer * Rng.Range(-1f, 1f);

            Heading = SenseMath.WrapPi(Heading + Math.Clamp(steer, -1f, 1f) * MaxTurn);

            float drive = _drive >= 0 ? action.Values[_drive] : 0f;
            // Скорость — модулятор тела: `Speed()` домножает шаг у ЛЮБОГО
            // вида, независимо от того, откуда пришла тяга (кровь или спайки).
            float step = drive * Creature.Blood.Speed() * MoveStepPerTick;
            float nx = X + MathF.Cos(Heading) * step;
            float ny = Y + MathF.Sin(Heading) * step;
            if (Inside(nx, ny))
            {
                X = nx;
                Y = ny;
            }
            else
            {
                // Боль приходит СЛЕДУЮЩИМ тиком: eligibility того поворота,
                // что привёл в стену, ещё горячая.
                X = Math.Clamp(nx, 0f, Width);
                Y = Math.Clamp(ny, 0f, Height);
                _pendingBlocked = true;
            }

            if (_eat >= 0 && action.IsSet(_eat))
            {
                for (int i = 0; i < Berries.Count; i++)
                {
                    var (bx, by) = Berries[i];
                    float dx = bx - X, dy = by - Y;
                    if (dx * dx + dy * dy <= SenseMath.EatRadius * SenseMath.EatRadius)
                    {
                        Berries[i] = (Rng.Range(2f, Width - 2f), Rng.Range(2f, Height - 2f));
                        TicksSinceReward = 0;
                        break;
                    }
                }
            }
            return action;
        }

        public float NearestBerryDistance()
        {
            float best = float.MaxValue;
            foreach (var (bx, by) in Berries)
            {
                float dx = bx - X, dy = by - Y;
                best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy));
            }
            return best == float.MaxValue ? 0f : best;
        }
    }
}
