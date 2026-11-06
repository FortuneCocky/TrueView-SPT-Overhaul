using System;

namespace EuphoriaReaction;

/// <summary>The reaction phase rules, checked without the game. Exit code 1 on any failure.</summary>
internal static class Program
{
    private static int _checks, _failed;

    private static void Check(string name, bool ok)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL " + name);
    }

    private static int Main()
    {
        // StumbleWindows
        Check("Short", PhaseRules.StumbleWindow(StumbleLength.Short, 9, 9) == (0.4f, 3.0f));
        Check("Natural", PhaseRules.StumbleWindow(StumbleLength.Natural, 9, 9) == (0.8f, 6.0f));
        Check("Long", PhaseRules.StumbleWindow(StumbleLength.Long, 9, 9) == (1.5f, 8.0f));
        Check("Custom", PhaseRules.StumbleWindow(StumbleLength.Custom, 1.0f, 4.0f) == (1.0f, 4.0f));
        // CustomMaxBelowMin
        var w = PhaseRules.StumbleWindow(StumbleLength.Custom, 2.0f, 1.0f);
        Check("CustomMaxBelowMin", w.min == 2.0f && Math.Abs(w.max - 2.2f) < 1e-4f);
        // StumbleDecision
        Check("TooEarly", PhaseRules.StumbleDecision(0.5f, 0.8f, 3f, true, false) == StumbleExit.Stay);
        Check("SteadyAfterMin", PhaseRules.StumbleDecision(0.9f, 0.8f, 3f, true, false) == StumbleExit.Recover);
        Check("Wobbling", PhaseRules.StumbleDecision(2.0f, 0.8f, 3f, false, false) == StumbleExit.Stay);
        Check("EasedBackAtMax", PhaseRules.StumbleDecision(3.0f, 0.8f, 3f, false, false) == StumbleExit.TooLong);
        Check("GoingDownWins", PhaseRules.StumbleDecision(0.1f, 0.8f, 3f, true, true) == StumbleExit.Fall);
        // FallDone
        Check("FallTooSoon", !PhaseRules.FallDone(0.2f, 0.2f));
        Check("FallStill", PhaseRules.FallDone(0.6f, 0.3f));
        Check("FallNotStillYet", !PhaseRules.FallDone(1.0f, 0.1f));
        Check("FallNeverStill", PhaseRules.FallDone(3.0f, 0f));
        // PoseLevels
        Check("PoseProne", PhaseRules.PoseFromGame(true, 1f, 0.85f) == StartPose.Prone);
        Check("PoseStand", PhaseRules.PoseFromGame(false, 0.85f, 0.85f) == StartPose.Stand);
        Check("PoseCrouch", PhaseRules.PoseFromGame(false, 0.84f, 0.85f) == StartPose.Crouch);
        Check("PoseDeep", PhaseRules.PoseFromGame(false, 0.25f, 0.85f) == StartPose.DeepCrouch);
        // HealthBoundaries
        Check("Healthy", PhaseRules.RecoverTarget(StartPose.Stand, 0.9f, 0.65f, 0.35f, false) == StartPose.Stand);
        Check("HurtAt65", PhaseRules.RecoverTarget(StartPose.Stand, 0.65f, 0.65f, 0.35f, false) == StartPose.Crouch);
        Check("HurtCrouch", PhaseRules.RecoverTarget(StartPose.Crouch, 0.5f, 0.65f, 0.35f, false) == StartPose.DeepCrouch);
        Check("ProneAt35", PhaseRules.RecoverTarget(StartPose.Stand, 0.35f, 0.65f, 0.35f, false) == StartPose.Prone);
        // LowStarts
        Check("HurtDeepStays", PhaseRules.RecoverTarget(StartPose.DeepCrouch, 0.5f, 0.65f, 0.35f, false) == StartPose.DeepCrouch);
        Check("HealthyProne", PhaseRules.RecoverTarget(StartPose.Prone, 1f, 0.65f, 0.35f, false) == StartPose.Prone);
        Check("EndedLowNotStand", PhaseRules.RecoverTarget(StartPose.Stand, 1f, 0.65f, 0.35f, true) == StartPose.Crouch);
        Check("EndedLowKeepsDeep", PhaseRules.RecoverTarget(StartPose.DeepCrouch, 1f, 0.65f, 0.35f, true) == StartPose.DeepCrouch);
        // Levels and names
        Check("Levels", PhaseRules.StanceLevel(StartPose.Stand) == 1f && PhaseRules.StanceLevel(StartPose.Crouch) == 0.5f && PhaseRules.StanceLevel(StartPose.DeepCrouch) == 0f);
        Check("Names", PhaseRules.Name(Phase.Stumble) == "stumble" && PhaseRules.Name(Phase.None) == "none");

        // HandBackSpring: the leftover offset at the hand-back dies out like a critically damped spring
        HandBackSpring.Coeffs(0.15f, 0f, out float a0, out float b0);
        Check("SpringStartsAtOffset", Math.Abs(a0 - 1f) < 1e-5f && Math.Abs(b0) < 1e-5f);
        HandBackSpring.Coeffs(0.15f, 0.005f, out float a1, out float b1);
        Check("SpringFromRestNoJump", a1 > 0.995f);
        Check("SpringCarriesVelocity", Math.Abs(b1 / 0.005f - 1f) < 0.05f);
        HandBackSpring.Coeffs(0.15f, HandBackSpring.Length(0.15f), out float aEnd, out float bEnd);
        Check("SpringGoneAtLength", aEnd < 0.01f && bEnd < 0.01f);
        bool falling = true; float prev = 1f;
        for (int i = 1; i <= 100; i++)
        {
            HandBackSpring.Coeffs(0.15f, i * 0.01f, out float ai, out _);
            if (ai > prev || ai < 0f) falling = false;
            prev = ai;
        }
        Check("SpringNoOvershoot", falling);
        // GetUpWarp: a get-up into a crouch bends into the animation's crouch over its rise instead of standing
        Check("WarpCrouchOnly", GetUpWarp.Applies(StartPose.Crouch) && GetUpWarp.Applies(StartPose.DeepCrouch)
                                && !GetUpWarp.Applies(StartPose.Stand) && !GetUpWarp.Applies(StartPose.Prone));
        Check("WarpNotWhileDown", GetUpWarp.Weight(20f, 60f, 90f) == 0f && GetUpWarp.Weight(36f, 60f, 90f) == 0f);
        Check("WarpFullWhenUp", GetUpWarp.Weight(60f, 60f, 90f) == 1f && GetUpWarp.Weight(89f, 60f, 90f) == 1f);
        float wMid = GetUpWarp.Weight(48f, 60f, 90f);
        Check("WarpMidRise", wMid > 0.3f && wMid < 0.7f);
        bool rising = true; float wPrev = 0f;
        for (int i = 0; i <= 90; i++)
        {
            float wi = GetUpWarp.Weight(i, 60f, 90f);
            if (wi < wPrev) rising = false;
            wPrev = wi;
        }
        Check("WarpNeverBacksOff", rising);
        Check("WarpStandAtEnd", GetUpWarp.Weight(90f, 120f, 90f) == 1f && GetUpWarp.Weight(40f, 120f, 90f) < 1f);
        Check("SpringHalflife",Math.Abs(HandBackSpring.Halflife(0.5f) - 0.15f) < 1e-5f && HandBackSpring.Halflife(0.1f) == HandBackSpring.MinHalflife);

        Console.WriteLine(_failed == 0 ? $"all {_checks} checks passed" : $"{_failed} of {_checks} checks failed");
        return _failed == 0 ? 0 : 1;
    }
}
