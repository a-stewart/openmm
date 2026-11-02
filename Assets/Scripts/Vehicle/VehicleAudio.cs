using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MM2.Vehicle
{
    [RequireComponent(typeof(ArcadeVehicleController))]
    public class VehicleAudio : MonoBehaviour
    {
        [Header("Audio Clips")]
        public AudioClip engineIdleClip;
        public AudioClip engineDriveClip;
        public AudioClip hornClip;

        [Header("Engine Pitch Settings")]
        [Tooltip("Minimum engine pitch at idle")]
        public float minPitch = 0.8f;
        [Tooltip("Maximum engine pitch at top speed")]
        public float maxPitch = 2.2f;

        private ArcadeVehicleController controller;
        private AudioSource idleSource;
        private AudioSource driveSource;
        private AudioSource hornSource;
        private AudioClip sirenClip;

        private void Awake()
        {
            controller = GetComponent<ArcadeVehicleController>();

            idleSource = gameObject.AddComponent<AudioSource>();
            idleSource.loop = true;
            idleSource.playOnAwake = false;
            idleSource.spatialBlend = 0.5f;

            driveSource = gameObject.AddComponent<AudioSource>();
            driveSource.loop = true;
            driveSource.playOnAwake = false;
            driveSource.spatialBlend = 0.5f;

            hornSource = gameObject.AddComponent<AudioSource>();
            hornSource.loop = false;
            hornSource.playOnAwake = false;
            hornSource.spatialBlend = 0.5f;

            if (engineIdleClip != null)
            {
                idleSource.clip = engineIdleClip;
                idleSource.Play();
            }

            if (engineDriveClip != null)
            {
                driveSource.clip = engineDriveClip;
                driveSource.Play();
            }

            if (hornClip != null)
            {
                hornSource.clip = hornClip;
            }
        }

        public void SwitchAudioClips(AudioClip newIdle, AudioClip newDrive, AudioClip newHorn)
        {
            if (newIdle != null)
            {
                engineIdleClip = newIdle;
                if (idleSource != null)
                {
                    idleSource.clip = newIdle;
                    if (gameObject.activeInHierarchy) idleSource.Play();
                }
            }

            if (newDrive != null)
            {
                engineDriveClip = newDrive;
                if (driveSource != null)
                {
                    driveSource.clip = newDrive;
                    if (gameObject.activeInHierarchy) driveSource.Play();
                }
            }

            if (newHorn != null)
            {
                hornClip = newHorn;
            }
            if (hornSource != null)
            {
                hornSource.Stop();
                hornSource.loop = false;
                hornSource.clip = newHorn;
            }
        }

        private void Update()
        {
            if (controller == null) return;

            var dmg = controller.VehicleDamage;
            if (dmg != null && dmg.IsTotaled)
            {
                if (idleSource != null) idleSource.volume = Mathf.MoveTowards(idleSource.volume, 0f, Time.deltaTime * 3f);
                if (driveSource != null) driveSource.volume = Mathf.MoveTowards(driveSource.volume, 0f, Time.deltaTime * 3f);
                if (hornSource != null && hornSource.isPlaying) hornSource.Stop();
                if (controller.sirensOn) controller.SetSirens(false);
                return;
            }

            float speedRatio = Mathf.Clamp01(controller.CurrentSpeedMph / controller.maxSpeedMph);
            float throttle = Mathf.Abs(controller.CurrentThrottle);

            // Dynamically blend pitch between idle and high rev
            float targetPitch = Mathf.Lerp(minPitch, maxPitch, (speedRatio * 0.7f) + (throttle * 0.3f));

            // Add subtle engine sputter/flutter when smoking & heavily damaged
            if (dmg != null && dmg.Damage01 > 0.60f)
            {
                float sputterStrength = Mathf.InverseLerp(0.60f, 1.0f, dmg.Damage01) * 0.14f;
                float flutter = Mathf.Sin(Time.time * 34f) * 0.6f + Mathf.Sin(Time.time * 53f) * 0.4f;
                targetPitch = Mathf.Max(0.45f, targetPitch + flutter * sputterStrength);
            }

            if (idleSource != null)
            {
                idleSource.pitch = Mathf.Lerp(idleSource.pitch, targetPitch, Time.deltaTime * 6f);
                idleSource.volume = Mathf.Lerp(0.8f, 0.2f, speedRatio);
            }

            if (driveSource != null)
            {
                driveSource.pitch = Mathf.Lerp(driveSource.pitch, targetPitch, Time.deltaTime * 6f);
                driveSource.volume = Mathf.Lerp(0.2f, 1.0f, (speedRatio * 0.6f) + (throttle * 0.4f));
            }

            // Horn / Police Siren Input
            bool hornPressed = false;
            bool hornJustPressed = false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.hKey.isPressed) hornPressed = true;
                if (kb.hKey.wasPressedThisFrame) hornJustPressed = true;
            }
            var gp = Gamepad.current;
            if (gp != null)
            {
                if (gp.buttonWest.isPressed) hornPressed = true;
                if (gp.buttonWest.wasPressedThisFrame) hornJustPressed = true;
            }
#else
            if (Input.GetKey(KeyCode.H)) hornPressed = true;
            if (Input.GetKeyDown(KeyCode.H)) hornJustPressed = true;
#endif
            if (controller.HasSirens)
            {
                if (hornJustPressed && !ArcadeVehicleController.IsInputLocked)
                {
                    controller.SetSirens(!controller.sirensOn);
                    if (hornSource != null)
                    {
                        if (controller.sirensOn)
                        {
                            if (sirenClip == null)
                            {
                                sirenClip = MM2.Archive.MM2RuntimeDecoders.LoadAudioClip("police1whailerloop.11k");
                                if (sirenClip == null)
                                    sirenClip = MM2.Archive.MM2RuntimeDecoders.LoadAudioClip("police2sirenloop.11k");
                            }
                            hornSource.clip = sirenClip != null ? sirenClip : hornClip;
                            hornSource.loop = true;
                            if (hornSource.clip != null && !hornSource.isPlaying)
                            {
                                hornSource.Play();
                            }
                        }
                        else
                        {
                            hornSource.loop = false;
                            if (hornSource.isPlaying) hornSource.Stop();
                        }
                    }
                }
            }
            else if (hornPressed && hornSource != null && !hornSource.isPlaying)
            {
                hornSource.loop = false;
                hornSource.Play();
            }
        }
    }
}
