using UnityEngine;
using System;

public class EventTest : MonoBehaviour
{
    public event Action OnSomething;
    float dMass;

    void OnEnable()
    {
        // Real event — SHOULD trigger UW-007 (no unsubscribe)
        OnSomething += Handle;

        // Arithmetic — must NOT trigger UW-007
        dMass += 1.5f;
        dMass += Time.deltaTime;
    }

    void Handle() { }
}
