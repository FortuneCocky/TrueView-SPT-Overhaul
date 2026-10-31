using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Downed on his back or side: instead of a get-up clip (it looked fake), he rolls onto his front and
/// is handed back prone, the stance closest to how he lies. The roll is physical: the higher shoulder
/// and hip are lifted up and over towards the side he already leans to, the curl is released so the
/// body can turn, and once he is face down the prone stance match takes over (prone pose, short blend).
/// A new hit stops the roll; if he can't get over in time he is handed back prone anyway.
/// </summary>
internal partial class NmBody
{
    private const float RollPush = 75f;   // N·s per second on each lifted point
    private const float RollPushFor = 1.2f; // s of lifting
    private const float RollMax = 2.2f;   // s before handing back prone regardless

    private bool _rolling, _rollLeftDown;
    private float _rollStart;
    private readonly float[] _rA = new float[3], _rI = new float[3];

    private bool BeginRoll()
    {
        if (_bones[13] == null || _bones[17] == null) return false;
        _rollLeftDown = _bones[13].position.y < _bones[17].position.y; // roll towards the side he already leans to
        StopFallenPose();
        Send("catchFall", ("start", false));
        _rolling = true;
        _rollStart = Time.time;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} rolling onto his front to go prone");
        return true;
    }

    /// Update: true while rolling (the rest of the tick is skipped).
    private bool RollTick()
    {
        if (!_rolling) return false;
        if (_lastHitAt > _rollStart) { _rolling = false; return false; }
        float t = Time.time - _rollStart;
        if (LyingFaceDown(out Vector3 headDir))
        {
            _rolling = false;
            if (!BeginMatch(StartPose.Prone, headDir) && HandOffProne()) { _blendDur = GetUpBlend; Release(); }
            return true;
        }
        if (t > RollMax)
        {
            _rolling = false;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} couldn't roll over - handed back prone");
            Vector3 h = _bones[12] != null && _bones[0] != null ? _bones[12].position - _bones[0].position : _p.Transform.forward;
            if (!BeginMatch(StartPose.Prone, h))
            {
                BotActions.SetPose(_p, 0f, true);
                _blendDur = GetUpBlend;
                Release();
            }
            return true;
        }
        if (t < RollPushFor)
        {
            int arm = _rollLeftDown ? 18 : 14, thigh = _rollLeftDown ? 4 : 1;     // the higher side
            Vector3 over = (_rollLeftDown ? _bones[13].position - _bones[17].position : _bones[17].position - _bones[13].position);
            over.y = 0f;
            Vector3 dir = (over.normalized * 0.7f + Vector3.up * 0.7f).normalized;
            float ramp = Mathf.SmoothStep(0f, 1f, t / 0.25f) * (1f - Mathf.SmoothStep(0f, 1f, (t - RollPushFor + 0.3f) / 0.3f));
            float push = RollPush * ramp * Time.deltaTime;
            for (int i = 0; i < 2; i++)
            {
                int k = i == 0 ? arm : thigh;
                Part(k, out Vector3 at, out _);
                ToNm(at, _rA); ToNm(dir * push, _rI);
                nm_agent_apply_impulse(_agent, k, _rA, _rI);
            }
        }
        return true;
    }
}
