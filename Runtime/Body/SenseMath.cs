// Эталонная сенсорная математика: как геометрия мира превращается в каналы
// `Sense`. Живёт в пакете, чтобы у игры и тестовой арены был ОДИН источник
// правды — расхождение формул означало бы, что тесты проверяют не то
// поведение, которое видит игрок.
//
// Мир сам считает расстояния и углы (у него есть коллайдеры, комнаты, тайлы),
// а эти функции переводят «дистанция + пеленг» в токи каналов.

using System;

namespace Dioma.Body
{
    public static class SenseMath
    {
        /// Спад запаха: strength = 60/(60+d) — далёкая еда пахнет слабо,
        /// но пахнет (в отличие от зрения с жёсткой дальностью).
        public const float SmellFalloff = 60.0f;
        /// Зрение: линейный спад до нуля и узкий конус.
        public const float VisionRange = 40.0f;
        public const float VisionHalfFov = 1.309f; // 75°
        /// Радиус укуса.
        public const float EatRadius = 2.0f;
        /// Дальность ощущения стен и шаг лучевой пробы.
        public const float WallLookahead = 8.0f;
        public const float WallProbeStep = 1.0f;

        /// Угол в (−π, π].
        public static float WrapPi(float a)
        {
            while (a > MathF.PI) a -= 2f * MathF.PI;
            while (a <= -MathF.PI) a += 2f * MathF.PI;
            return a;
        }

        /// Вклад одной единицы еды в каналы запаха/зрения.
        ///
        /// `rel` — пеленг на еду относительно курса, радианы, положительный
        /// против часовой (как `atan2(dy, dx) − heading`). Множитель `sin`
        /// делает запах ГРАДИЕНТОМ: прямо по курсу оба канала почти нулевые.
        /// Существо различает сторону, а не наличие еды — асимметрия каналов
        /// и есть сигнал «поворачивай», симметрия значит «иду верно».
        public static void AddFood(ref Sense s, float rel, float dist)
        {
            float strength = Math.Clamp(SmellFalloff / (SmellFalloff + dist), 0f, 1f);
            if (rel < 0f) s.SmellL += strength * MathF.Max(-MathF.Sin(rel), 0f);
            else s.SmellR += strength * MathF.Max(MathF.Sin(rel), 0f);

            // Зрение дальнобойнее обоняния, но только в конусе: ловит еду,
            // которую «ветер несёт мимо носа».
            if (MathF.Abs(rel) <= VisionHalfFov)
            {
                float vis = Math.Clamp(1f - dist / VisionRange, 0f, 1f);
                if (rel < 0f) s.VisL += vis;
                else s.VisR += vis;
            }

            if (dist <= EatRadius) s.FoodNear = true;
        }

        /// Каналы насыщаются: несколько ягод не дают тока больше одного.
        public static void Saturate(ref Sense s)
        {
            s.SmellL = MathF.Min(s.SmellL, 1f);
            s.SmellR = MathF.Min(s.SmellR, 1f);
            s.VisL = MathF.Min(s.VisL, 1f);
            s.VisR = MathF.Min(s.VisR, 1f);
        }

        /// Близость стены по дистанции до неё: 1 у самой стены, 0 на пределе
        /// ощущения. Отрицательная дистанция или «стены нет» → 0.
        public static float WallProximity(float dist)
        {
            if (dist < 0f || dist > WallLookahead) return 0f;
            return 1f - dist / WallLookahead;
        }
    }
}
