using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using MM2.Vehicle;
using MM2.CameraSystem;

namespace MM2.Editor
{
    public static class PlayableSceneSetupUtility
    {
        [MenuItem("OpenMadness/Setup Playable GoDrive Scene")]
        public static void BuildPlayableScene()
        {
            Debug.Log("[OpenMadness] Setting up GoDrive scene (runtime .ar city & vehicle loader)...");

            string scenePath = "Assets/Scenes/GoDrive.unity";
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            ConfigureDefaultLighting();

            Vector3 spawnPos = new Vector3(1152.0f, 65.25f, -402.12f);
            Quaternion spawnRot = Quaternion.Euler(0f, 90f, 0f);

            GameObject car = new GameObject("PlayerVehicle");
            car.transform.SetPositionAndRotation(spawnPos, spawnRot);
            car.tag = "Player";

            var rb = car.AddComponent<Rigidbody>();
            rb.mass = 1300f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var boxCol = car.AddComponent<BoxCollider>();
            boxCol.size = new Vector3(1.87f, 1.22f, 4.77f);
            boxCol.center = new Vector3(0f, 0.77f, -0.06f);

            var controller = car.AddComponent<ArcadeVehicleController>();
            var paintjob = car.AddComponent<VehiclePaintjob>();
            paintjob.currentVehicle = "vpmustang99";
            paintjob.currentColor = "Default";
            car.AddComponent<VehicleAudio>();

            // Setup 4 Wheel Colliders (visual meshes, wheel positions, and audio are populated at runtime from .ar archives)
            GameObject wheelColsRoot = new GameObject("WheelColliders");
            wheelColsRoot.transform.SetParent(car.transform, false);

            Vector3 posFL = new Vector3(-0.735f, 0.323f, 1.404f);
            Vector3 posFR = new Vector3( 0.738f, 0.323f, 1.404f);
            Vector3 posRL = new Vector3(-0.733f, 0.323f, -1.427f);
            Vector3 posRR = new Vector3( 0.733f, 0.323f, -1.427f);

            controller.frontLeftCollider = CreateWheelCollider(wheelColsRoot.transform, "FL_Col", posFL);
            controller.frontRightCollider = CreateWheelCollider(wheelColsRoot.transform, "FR_Col", posFR);
            controller.rearLeftCollider = CreateWheelCollider(wheelColsRoot.transform, "RL_Col", posRL);
            controller.rearRightCollider = CreateWheelCollider(wheelColsRoot.transform, "RR_Col", posRR);

            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.farClipPlane = 6000f;
                mainCam.nearClipPlane = 0.25f;
                var chaseCam = mainCam.gameObject.AddComponent<ArcadeChaseCamera>();
                chaseCam.target = car.transform;
                chaseCam.targetRigidbody = rb;
                mainCam.transform.position = car.transform.TransformPoint(chaseCam.nearOffset);
                Vector3 lookTarget = car.transform.position + (car.transform.forward * 2.0f) + (Vector3.up * 1.0f);
                mainCam.transform.LookAt(lookTarget, Vector3.up);
            }

            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MM2] Saved GoDrive scene to: {scenePath}");
        }

        private static void ConfigureDefaultLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.68f, 0.76f, 0.88f);
            RenderSettings.ambientEquatorColor = new Color(0.58f, 0.60f, 0.64f);
            RenderSettings.ambientGroundColor = new Color(0.42f, 0.42f, 0.45f);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.76f, 0.85f, 0.95f);
            RenderSettings.fogStartDistance = 900f;
            RenderSettings.fogEndDistance = 4200f;

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light l in lights)
            {
                if (l.type == LightType.Directional)
                {
                    l.transform.rotation = Quaternion.Euler(46f, -42f, 0f);
                    l.color = new Color(1.0f, 0.97f, 0.91f);
                    l.intensity = 1.25f;
                    l.shadows = LightShadows.Soft;
                }
            }
        }

        private static WheelCollider CreateWheelCollider(Transform parent, string name, Vector3 localPos)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var wc = go.AddComponent<WheelCollider>();
            wc.radius = 0.346f;
            wc.suspensionDistance = 0.22f;
            wc.forceAppPointDistance = 0f;
            wc.mass = 45f;
            wc.wheelDampingRate = 0.25f;

            JointSpring spring = wc.suspensionSpring;
            spring.spring = 38000f;
            spring.damper = 3500f;
            spring.targetPosition = 0.5f;
            wc.suspensionSpring = spring;

            WheelFrictionCurve fwd = wc.forwardFriction;
            fwd.extremumSlip = 0.4f;
            fwd.extremumValue = 1.0f;
            fwd.asymptoteSlip = 0.8f;
            fwd.asymptoteValue = 0.65f;
            fwd.stiffness = 1.2f;
            wc.forwardFriction = fwd;

            WheelFrictionCurve side = wc.sidewaysFriction;
            side.extremumSlip = 0.25f;
            side.extremumValue = 1.0f;
            side.asymptoteSlip = 0.5f;
            side.asymptoteValue = 0.75f;
            side.stiffness = 1.0f;
            wc.sidewaysFriction = side;

            return wc;
        }
    }
}
