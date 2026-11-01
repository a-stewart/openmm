using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MM2.CameraSystem
{
    public class ArcadeChaseCamera : MonoBehaviour
    {
        public enum CameraMode
        {
            ChaseNear,
            ChaseFar,
            Bumper
        }

        [Header("Target Target")]
        public Transform target;
        public Rigidbody targetRigidbody;

        [Header("Camera Modes Settings")]
        public CameraMode currentMode = CameraMode.ChaseNear;

        [Header("Near Chase")]
        public Vector3 nearOffset = new Vector3(0f, 2.75f, -5.0f);

        [Header("Far Chase")]
        public Vector3 farOffset = new Vector3(0f, 3.65f, -6.6f);

        [Header("Bumper Cam")]
        public Vector3 bumperOffset = new Vector3(0f, 1.10f, 1.2f);

        [Header("Damping & Speed Elasticity")]
        public float followPositionDamping = 9.0f;
        public float followRotationDamping = 6.5f;
        public float speedElasticity = 0.02f;

        private float postEventElapsed;
        private float smoothedDownhillSlope;

        private void LateUpdate()
        {
            if (target == null) return;

            var raceMgr = MM2.UI.MM2RaceManager.Instance;
            bool isSinking = raceMgr != null && raceMgr.IsSinkingInWater;
            bool isFinished = raceMgr != null && raceMgr.IsRaceFinished;
            bool isTotaled = raceMgr != null && raceMgr.IsPlayerTotaled;
            bool isCruiseZoomingIn = raceMgr != null && raceMgr.IsCruiseRepairZoomingIn;

            if (isSinking || isFinished || isTotaled || isCruiseZoomingIn)
            {
                float smoothT;
                float sideAngleDeg;

                if (isCruiseZoomingIn)
                {
                    // Reverse the zoom-out orbit smoothly back to the normal chase camera after Cruise auto-repair
                    float prog = raceMgr.CruiseRepairZoomInProgress01;
                    float rev = 1f - (prog * prog * (3f - 2f * prog));
                    smoothT = rev;
                    sideAngleDeg = (52f + postEventElapsed * 6.5f) * rev;
                }
                else
                {
                    postEventElapsed += Time.deltaTime;
                    float tZoom = Mathf.Clamp01(postEventElapsed / 2.2f);
                    smoothT = tZoom * tZoom * (3f - 2f * tZoom);
                    sideAngleDeg = Mathf.Lerp(0f, isSinking ? 62f : 52f, smoothT) + (postEventElapsed * 6.5f);
                }

                // Smoothly zoom out and rotate around the car so the camera looks more at the side/three-quarter profile
                // (matching Screenshot 1 for sinking in water and Screenshot 2 for finishing a race / timing out / totaling).
                float startDist = (currentMode == CameraMode.ChaseFar) ? 6.5f : 4.8f;
                float endDist = isSinking ? 10.8f : 9.4f;
                float dist = Mathf.Lerp(startDist, endDist, smoothT);

                float startHeight = (currentMode == CameraMode.ChaseFar) ? 3.05f : 2.25f;
                float endHeight = isSinking ? 4.6f : 4.3f;
                float height = Mathf.Lerp(startHeight, endHeight, smoothT);

                Quaternion orbitRot = Quaternion.Euler(0f, target.eulerAngles.y + sideAngleDeg, 0f);
                Vector3 worldOffset = orbitRot * new Vector3(0f, height, -dist);

                Vector3 focusCenter = target.position;
                if (isSinking)
                {
                    // Keep the camera above the water surface looking down at the sinking car
                    focusCenter.y = Mathf.Max(target.position.y, raceMgr.WaterSurfaceY - 0.6f);
                }

                Vector3 desiredPos = focusCenter + worldOffset;
                if (isSinking)
                {
                    desiredPos.y = Mathf.Max(desiredPos.y, raceMgr.WaterSurfaceY + 3.0f);
                }
                else
                {
                    desiredPos = EnsureAboveGround(target.position, desiredPos, 1.15f);
                }

                desiredPos = PreventBuildingAndTerrainClipping(focusCenter, desiredPos, minDistance: 2.2f);

                float posLerpRate = isCruiseZoomingIn ? 8.5f : 4.5f;
                float rotLerpRate = isCruiseZoomingIn ? 9.0f : 5.0f;

                Vector3 nextPos = Vector3.Lerp(transform.position, desiredPos, Time.deltaTime * posLerpRate);
                if (!isSinking)
                {
                    nextPos = EnsureAboveGround(target.position, nextPos, 1.15f);
                }
                transform.position = PreventBuildingAndTerrainClipping(focusCenter, nextPos, minDistance: 2.2f);

                Vector3 lookTarget = focusCenter + Vector3.up * (isSinking ? 0.45f : 0.75f);
                Vector3 toLook = lookTarget - transform.position;
                if (toLook.sqrMagnitude > 0.001f)
                {
                    Quaternion desiredRot = Quaternion.LookRotation(toLook, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, Time.deltaTime * rotLerpRate);
                }
                return;
            }

            postEventElapsed = 0f;
            CheckModeSwitch();

            // Ensure scene-serialized camera offsets use the raised vertical vantage point
            if (nearOffset.y < 2.75f) nearOffset.y = 2.75f;
            if (farOffset.y < 3.65f) farOffset.y = 3.65f;

            Vector3 baseOffset;
            switch (currentMode)
            {
                case CameraMode.Bumper:
                    baseOffset = bumperOffset;
                    break;
                case CameraMode.ChaseFar:
                    baseOffset = farOffset;
                    break;
                default:
                    baseOffset = nearOffset;
                    break;
            }

            // Speed pull-back effect for sensation of speed
            Vector3 vel = (targetRigidbody != null) ? targetRigidbody.linearVelocity : Vector3.zero;
            float speed = vel.magnitude;
            Vector3 dynamicOffset = baseOffset;
            if (currentMode != CameraMode.Bumper)
            {
                dynamicOffset.z -= speed * speedElasticity;
            }

            if (currentMode == CameraMode.Bumper)
            {
                transform.position = target.TransformPoint(dynamicOffset);
                transform.rotation = target.rotation;
            }
            else
            {
                // Blend camera trailing direction between car heading and horizontal velocity vector (camTrackCS)
                // so when the car kicks its tail out in a drift, the camera reveals the side/three-quarter profile.
                Vector3 carFwdFlat = new Vector3(target.forward.x, 0f, target.forward.z);
                if (carFwdFlat.sqrMagnitude < 0.001f) carFwdFlat = Vector3.forward;
                else carFwdFlat.Normalize();

                Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
                float horizSpeed = horizVel.magnitude;
                Vector3 trailDir = carFwdFlat;

                if (horizSpeed > 3.5f)
                {
                    Vector3 velDir = horizVel / horizSpeed;
                    float fwdAlign = Mathf.Clamp01((Vector3.Dot(velDir, carFwdFlat) + 0.35f) / 0.65f);
                    float slipBlend = Mathf.Clamp01((horizSpeed - 3.5f) / 10f) * fwdAlign * 0.68f;
                    trailDir = Vector3.Slerp(carFwdFlat, velDir, slipBlend).normalized;
                }

                // Detect downhill slope (both from car pitch and velocity vector) so the camera rises above the
                // hill behind the car and tilts down at an angle even steeper than the car.
                float carDownSlope = Mathf.Max(0f, -target.forward.y);
                float velDownSlope = (speed > 2.5f) ? Mathf.Max(0f, -vel.y / speed) : 0f;
                float rawDownhillSlope = Mathf.Max(carDownSlope, velDownSlope);
                float slopeLerpRate = (rawDownhillSlope > smoothedDownhillSlope) ? 10.0f : 4.5f;
                smoothedDownhillSlope = Mathf.Lerp(smoothedDownhillSlope, rawDownhillSlope, Time.deltaTime * slopeLerpRate);

                float followDist = Mathf.Abs(dynamicOffset.z);
                // Lift camera by the hill rise behind the car PLUS an extra 65% downhill elevation boost
                // so the camera's downward angle is always steeper than the car on downhills.
                float downhillHeightBoost = smoothedDownhillSlope * followDist * 1.65f;
                dynamicOffset.y += downhillHeightBoost;

                Quaternion trailRot = Quaternion.LookRotation(trailDir, Vector3.up);
                Vector3 targetPosition = target.position + trailRot * dynamicOffset;
                targetPosition = EnsureAboveGround(target.position, targetPosition, baseOffset.y * 0.65f);
                targetPosition = PreventBuildingAndTerrainClipping(target.position, targetPosition, minDistance: 2.0f);

                Vector3 nextPos = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * followPositionDamping);
                nextPos = EnsureAboveGround(target.position, nextPos, 1.15f);
                transform.position = PreventBuildingAndTerrainClipping(target.position, nextPos, minDistance: 2.0f);

                // Pitch lookTarget down the hill when going downhill (and slightly up on uphills)
                // so the camera looks down over the car's roof onto the downhill road ahead.
                float lookForwardDist = 2.8f;
                float slopePitchY = Mathf.Min(0f, target.forward.y) * lookForwardDist * 1.35f +
                                    Mathf.Max(0f, target.forward.y) * lookForwardDist * 0.65f;
                Vector3 lookTarget = target.position + (trailDir * lookForwardDist) + Vector3.up * (0.85f + slopePitchY);
                Quaternion targetRot = Quaternion.LookRotation(lookTarget - transform.position, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * followRotationDamping);
            }
        }

        private Vector3 EnsureAboveGround(Vector3 carPos, Vector3 camPos, float minClearance)
        {
            Transform carRoot = target != null ? target.root : null;

            // 1. Linecast from above the car to the camera to prevent clipping into steep hill crests behind the car
            Vector3 anchor = carPos + Vector3.up * 1.45f;
            Vector3 toCam = camPos - anchor;
            float dist = toCam.magnitude;
            if (dist > 0.2f)
            {
                RaycastHit[] lineHits = Physics.SphereCastAll(
                    anchor,
                    0.28f,
                    toCam / dist,
                    dist,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                for (int i = 0; i < lineHits.Length; i++)
                {
                    RaycastHit hit = lineHits[i];
                    if (carRoot != null && hit.transform.root == carRoot) continue;
                    // If a ground/hill surface is hit (normal pointing mostly upward), lift camera above that surface
                    if (hit.normal.y > 0.35f)
                    {
                        float requiredY = hit.point.y + minClearance;
                        if (camPos.y < requiredY) camPos.y = requiredY;
                    }
                }
            }

            // 2. Vertical raycast from above the camera downward to ensure camera stays above the road/terrain beneath it
            Vector3 rayOrigin = new Vector3(camPos.x, Mathf.Max(camPos.y + 6.0f, carPos.y + 8.0f), camPos.z);
            RaycastHit[] downHits = Physics.RaycastAll(
                rayOrigin,
                Vector3.down,
                16.0f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < downHits.Length; i++)
            {
                RaycastHit hit = downHits[i];
                if (carRoot != null && hit.transform.root == carRoot) continue;
                if (hit.normal.y > 0.35f && hit.point.y < carPos.y + 6.5f)
                {
                    float requiredY = hit.point.y + minClearance;
                    if (camPos.y < requiredY) camPos.y = requiredY;
                }
            }

            return camPos;
        }

        /// <summary>
        /// Casts from above the vehicle center toward the desired camera position and stops the camera
        /// in front of any building facade, tunnel wall/ceiling, bridge abutment, or terrain face.
        /// </summary>
        private Vector3 PreventBuildingAndTerrainClipping(Vector3 focusPos, Vector3 camPos, float minDistance)
        {
            Transform carRoot = target != null ? target.root : null;
            Vector3 anchor = focusPos + Vector3.up * 1.25f;
            Vector3 toCam = camPos - anchor;
            float dist = toCam.magnitude;
            if (dist <= 0.25f) return camPos;

            Vector3 dir = toCam / dist;
            const float probeRadius = 0.28f;
            const float wallClearance = 0.38f;

            bool prevBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                float closestHitDist = dist;
                Vector3 closestHitNormal = -dir;
                bool foundObstacle = false;

                // 1. Raycast catches single-sided building walls or terrain faces even at very close range
                RaycastHit[] rayHits = Physics.RaycastAll(
                    anchor,
                    dir,
                    dist,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                for (int i = 0; i < rayHits.Length; i++)
                {
                    RaycastHit hit = rayHits[i];
                    if (ShouldIgnoreCameraObstacle(hit.collider, carRoot)) continue;
                    if (hit.distance < closestHitDist)
                    {
                        closestHitDist = hit.distance;
                        closestHitNormal = hit.normal;
                        foundObstacle = true;
                    }
                }

                // 2. SphereCast keeps a safety margin around the camera lens near building corners and ledges
                RaycastHit[] sphereHits = Physics.SphereCastAll(
                    anchor,
                    probeRadius,
                    dir,
                    dist,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);

                for (int i = 0; i < sphereHits.Length; i++)
                {
                    RaycastHit hit = sphereHits[i];
                    if (ShouldIgnoreCameraObstacle(hit.collider, carRoot)) continue;
                    if (hit.distance < closestHitDist)
                    {
                        closestHitDist = hit.distance;
                        closestHitNormal = hit.normal;
                        foundObstacle = true;
                    }
                }

                if (foundObstacle)
                {
                    float clampedDist = Mathf.Max(minDistance, closestHitDist - wallClearance);
                    clampedDist = Mathf.Min(clampedDist, closestHitDist);
                    camPos = anchor + dir * clampedDist;

                    // Nudge slightly away from the surface normal so the camera near clip plane doesn't peek inside the wall
                    if ( Vector3.Dot(closestHitNormal, -dir) > 0f )
                    {
                        camPos += closestHitNormal * 0.12f;
                    }
                    else
                    {
                        camPos -= closestHitNormal * 0.12f;
                    }
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = prevBackfaces;
            }

            return camPos;
        }

        private static bool ShouldIgnoreCameraObstacle(Collider col, Transform carRoot)
        {
            if (col == null) return true;
            if (carRoot != null && col.transform.root == carRoot) return true;
            // Ignore dynamic vehicles (traffic, cops, opponents) and breakable sidewalk props
            if (col.attachedRigidbody != null) return true;
            if (col.GetComponentInParent<MM2.Archive.MM2BangerProp>() != null) return true;
            return false;
        }

        private void CheckModeSwitch()
        {
            bool switchView = false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.cKey.wasPressedThisFrame) switchView = true;
            var gp = Gamepad.current;
            if (gp != null && gp.buttonNorth.wasPressedThisFrame) switchView = true;
#else
            if (Input.GetKeyDown(KeyCode.C)) switchView = true;
#endif
            if (switchView)
            {
                currentMode = (CameraMode)(((int)currentMode + 1) % 3);
            }
        }
    }
}
