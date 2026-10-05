Gesture Dictionary — v1 (versioned contract)
Locked by LIGHTWEIGHT XR CONTENT ENGINE rev3, §2. Changes only via manifest versioning.
-	SwipeRight 
Next XR layer. Cycles 1→2→…→N. A swipe after the last layer dismisses everything.
-	SwipeLeft
Previous XR layer.
-	PalmHold
Dismiss current layer immediately (open palm held still ~0.8 s).
-	Pinch
RESERVED — select/confirm in future versions. Not bound in v1.

Classifier notes (for the team)
Platform gives raw 26-joint poses only (OpenXR hand tracking). There are no system "swipe" events.
Swipe: velocity-vector analysis of palm travel in head space. Tunables in GestureDictionary.SwipeTuning: min 0.22 m horizontal travel, max 0.60 s duration, max 0.15 m vertical drift, 0.50 s debounce. Tune on the team's collected gesture dataset.
PalmHold: palm position variance under threshold for 0.80 s while tracked. v1 approximation — add finger-extension check during dataset tuning.
Budget: gesture detect → visible layer switch must stay under 100 ms (LayerSwitcher logs a warning when exceeded).
