using System.Collections.Generic;
using UnityEngine;

namespace BloodFX;

/// <summary>Receives the volumetric blob particles' world collisions and hands each contact to the manager.</summary>
internal sealed class BlobHits : MonoBehaviour
{
    private readonly List<ParticleCollisionEvent> _events = new List<ParticleCollisionEvent>(64);
    private ParticleSystem _ps;
    private BloodManager _owner;

    public void Init(ParticleSystem ps, BloodManager owner)
    {
        _ps = ps;
        _owner = owner;
    }

    private void OnParticleCollision(GameObject other)
    {
        if (_ps == null || _owner == null) return;
        int n = _ps.GetCollisionEvents(other, _events);
        for (int i = 0; i < n; i++)
        {
            ParticleCollisionEvent e = _events[i];
            _owner.OnBlobLanded(e.intersection, e.normal, e.velocity);
        }
    }
}
