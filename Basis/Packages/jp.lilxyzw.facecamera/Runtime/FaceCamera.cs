using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace jp.lilxyzw.facecamera
{
    public static class FaceCamera
    {
        private static RenderTexture renderTexture;
        private static Material material;
        private static GameObject ui;
        private static Camera camera;
        private static MeshRenderer renderer;

        public static void InitializeFaceCam()
        {
            FaceCameraSettings.LoadAll();

            if (!BasisLocalCameraDriver.CameraInstance || !FaceCameraSettings.EnableFaceCamera.RawValue)
            {
                if (camera) camera.enabled = false;
                return;
            }

            if (!renderTexture) renderTexture = new(256, 256, 24, RenderTextureFormat.ARGB64, 0);

            if (!material) material = new Material(Shader.Find("Hidden/FaceCameraShader"));
            material.mainTexture = renderTexture;

            if (!ui)
            {
                ui = new("FaceCamUI");
                ui.layer = 9; // OverlayUI

                ui = GameObject.CreatePrimitive(PrimitiveType.Quad);
                renderer = ui.GetComponent<MeshRenderer>();
                Object.Destroy(ui.GetComponent<MeshCollider>());
                renderer.allowOcclusionWhenDynamic = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.receiveShadows = false;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.sharedMaterial = material;
            }

            renderer.enabled = true;

            ui.transform.parent = BasisLocalCameraDriver.CameraInstance.transform;
            ui.transform.SetLocalPositionAndRotation(Vector3.forward * 0.25f, Quaternion.identity);

            UpdateUI();

            if (!camera)
            {
                camera = new GameObject("FaceCam").AddComponent<Camera>();

                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.orthographic = true;
                camera.fieldOfView = 10;
                camera.cullingMask = 1 << 6; // LocalPlayerAvatar
                camera.useOcclusionCulling = false;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.allowDynamicResolution = false;
                camera.depth = 1;

                var cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.renderShadows = false;
                cameraData.requiresDepthTexture = false;
                cameraData.requiresColorTexture = false;
                cameraData.volumeLayerMask = 0;
            }

            camera.gameObject.SetActive(true);
            camera.targetTexture = renderTexture;
            camera.enabled = true;
            camera.GetUniversalAdditionalCameraData().enabled = true;

            if (BasisLocalPlayer.Instance && BasisLocalPlayer.Instance.BasisAvatar && BasisLocalAvatarDriver.Mapping != null)
            {
                camera.orthographicSize = BasisLocalPlayer.Instance.BasisAvatar.AvatarEyePosition.x * FaceCameraSettingsForAvatar.GetRange();
                camera.nearClipPlane = -10;
                camera.farClipPlane = 10;
                var head = BasisLocalAvatarDriver.Mapping.head;
                var lefteye = BasisLocalAvatarDriver.Mapping.LeftEye;
                var righteye = BasisLocalAvatarDriver.Mapping.RightEye;
                if (head) camera.transform.parent = head;

                var centerpos = new Vector3(0,FaceCameraSettingsForAvatar.GetOffsetY(),1);
                if (head && lefteye && righteye) centerpos.y += Vector3.Distance((lefteye.position + righteye.position) / 2, head.position) / 2;

                camera.transform.SetLocalPositionAndRotation(centerpos, Quaternion.identity);
                camera.transform.Rotate(Vector3.up, 180, Space.Self);
            }
        }

        public static void UpdateUI()
        {
            if (!material)
            {
                InitializeFaceCam();
                return;
            }

            if (camera) camera.enabled = FaceCameraSettings.EnableFaceCamera.RawValue;

            material.SetFloat("_FaceCameraOpacity", FaceCameraSettings.Opacity.RawValue);
            material.SetFloat("_FaceCameraSizeVR", FaceCameraSettings.SizeVR.RawValue);
            material.SetFloat("_FaceCameraOffsetXVR", FaceCameraSettings.OffsetXVR.RawValue);
            material.SetFloat("_FaceCameraOffsetYVR", FaceCameraSettings.OffsetYVR.RawValue);
            material.SetFloat("_FaceCameraSizeDesktop", FaceCameraSettings.SizeDesktop.RawValue);
            material.SetFloat("_FaceCameraOffsetXDesktop", FaceCameraSettings.OffsetXDesktop.RawValue);
            material.SetFloat("_FaceCameraOffsetYDesktop", FaceCameraSettings.OffsetYDesktop.RawValue);

            Vector2 origin = FaceCameraSettings.Anchor.RawValue switch
            {
                nameof(AnchorType.LeftTop) => new(1,-1),
                nameof(AnchorType.RightTop) => new(-1,-1),
                nameof(AnchorType.LeftBottom) => new(1,1),
                nameof(AnchorType.RightBottom) => new(-1,1),
                _ => new(0,0)
            };

            material.SetFloat("_FaceCameraAnchorX", origin.x);
            material.SetFloat("_FaceCameraAnchorY", origin.y);
        }

        [RuntimeInitializeOnLoadMethod]
        private static void Initialize()
        {
            BasisLocalPlayer.OnLocalPlayerInitialized += InitializeFaceCam;
            BasisLocalPlayer.OnLocalAvatarChanged += InitializeFaceCam;
            RenderPipelineManager.beginCameraRendering += (_,ctxCamera) =>
            {
                if (ctxCamera == camera)
                {
                    BasisLocalAvatarDriver.ScaleHeadToNormal();
                }
                else
                {
                    if (ui) ui.SetActive(FaceCameraSettings.EnableFaceCamera.RawValue && ctxCamera == BasisLocalCameraDriver.CameraInstance);
                }
            };
        }
    }
}
