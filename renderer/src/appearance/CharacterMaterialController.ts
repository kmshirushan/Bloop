import * as THREE from 'three';
import { BloopModel } from '../scene/BloopModel';
import { CharacterAppearanceConfig } from './AppearanceTypes';

interface GlassShaderUniforms {
  uGlassMode: { value: number };
  uCoreOpacity: { value: number };
  uRimOpacity: { value: number };
  uGlowBoost: { value: number };
  uAccentColor: { value: THREE.Color };
  uBodyColor: { value: THREE.Color };
}

/**
 * Updates Bloop's body and eye materials in-place without recreating Three.js materials.
 * For Glass and LiquidGlass presets, injects a DirectComposition-safe Fresnel + Beer-Lambert
 * optical depth & internal caustic shader chunk via onBeforeCompile so the center of the
 * sphere is optically clear/refractive while the grazing rim is dense, specular, and luminous.
 * Classic, Matte, Metallic, and Soft presets execute standard unmodified physical shading (uGlassMode = 0).
 */
export class CharacterMaterialController {
  private readonly tempEmissive = new THREE.Color();
  private readonly tempSheen = new THREE.Color();

  private readonly glassUniforms: GlassShaderUniforms = {
    uGlassMode: { value: 0.0 },
    uCoreOpacity: { value: 1.0 },
    uRimOpacity: { value: 1.0 },
    uGlowBoost: { value: 0.35 },
    uAccentColor: { value: new THREE.Color('#38BDF8') },
    uBodyColor: { value: new THREE.Color('#0284C7') }
  };

  constructor(private readonly model: BloopModel) {
    this.installGlassShaderHook();
  }

  private installGlassShaderHook(): void {
    const mat = this.model.bodyMesh.material;
    mat.customProgramCacheKey = () => 'bloop-liquid-glass-v1';

    mat.onBeforeCompile = (shader) => {
      shader.uniforms.uGlassMode = this.glassUniforms.uGlassMode;
      shader.uniforms.uCoreOpacity = this.glassUniforms.uCoreOpacity;
      shader.uniforms.uRimOpacity = this.glassUniforms.uRimOpacity;
      shader.uniforms.uGlowBoost = this.glassUniforms.uGlowBoost;
      shader.uniforms.uAccentColor = this.glassUniforms.uAccentColor;
      shader.uniforms.uBodyColor = this.glassUniforms.uBodyColor;

      shader.fragmentShader =
        `
        uniform float uGlassMode;
        uniform float uCoreOpacity;
        uniform float uRimOpacity;
        uniform float uGlowBoost;
        uniform vec3 uAccentColor;
        uniform vec3 uBodyColor;
        ` + shader.fragmentShader;

      shader.fragmentShader = shader.fragmentShader.replace(
        '#include <premultiplied_alpha_fragment>',
        `
        if ( uGlassMode > 0.5 ) {
          vec3 viewDir = normalize( vViewPosition );
          vec3 worldNormal = normalize( vNormal );
          float ndotv = clamp( abs( dot( worldNormal, viewDir ) ), 0.0, 1.0 );

          // Schlick-inspired Fresnel edge curve (sharper for Glass, fuller liquid meniscus for LiquidGlass)
          float fresnelExp = ( uGlassMode > 1.5 ) ? 2.35 : 2.95;
          float fresnel = pow( 1.0 - ndotv, fresnelExp );
          float outerRim = pow( 1.0 - ndotv, 4.6 );

          // Internal bottom-hemisphere caustic focus + top meniscus highlight
          float lowerCaustic = pow( clamp( -worldNormal.y * 0.65 + 0.38, 0.0, 1.0 ), 2.2 ) * ( 1.0 - fresnel * 0.55 );
          float topCrescent = pow( clamp( worldNormal.y * 0.72 + 0.28, 0.0, 1.0 ), 3.2 ) * fresnel;

          // Deep sapphire absorption in the core so specular highlights pop with high contrast
          vec3 deepCore = uBodyColor * 0.42;
          vec3 causticGlow = mix( uBodyColor, uAccentColor, 0.65 ) * ( lowerCaustic * ( 0.38 + uGlowBoost * 0.32 ) );
          vec3 rimLight = mix( uAccentColor, vec3( 0.92, 0.98, 1.0 ), outerRim * 0.58 ) * ( fresnel * ( 0.75 + uGlowBoost * 0.42 ) );
          vec3 crescentLight = vec3( 0.88, 0.96, 1.0 ) * ( topCrescent * 0.48 );

          // Blend physical lighting with deep glass core & Fresnel rim
          gl_FragColor.rgb = gl_FragColor.rgb * mix( 0.54, 1.05, fresnel ) + deepCore * ( 1.0 - fresnel ) * 0.28 + causticGlow + rimLight + crescentLight;

          // View-dependent optical thickness: clear center -> dense refractive glass edge
          gl_FragColor.a = clamp( mix( uCoreOpacity, uRimOpacity, smoothstep( 0.0, 0.92, fresnel ) ) + outerRim * 0.12, 0.22, 0.98 );
        }
        #include <premultiplied_alpha_fragment>
        `
      );
    };
  }

  public apply(config: CharacterAppearanceConfig): void {
    const bodyColor = config.bodyColor || '#0284C7';
    const accentColor = config.accentColor || '#38BDF8';

    // 1. Update vertex-color vertical gradient on sphere geometry in-place
    this.model.updateBodyGradient(bodyColor, accentColor);

    // 2. Update body MeshPhysicalMaterial in-place
    const mat = this.model.bodyMesh.material;
    const preset = config.preset || 'Classic';
    const isGlass = preset === 'Glass' || preset === 'LiquidGlass';
    const isLiquid = preset === 'LiquidGlass';
    const isSoft = preset === 'Soft';
    const isMatte = preset === 'Matte';
    const isMetallic = preset === 'Metallic';

    const clampedRoughness = THREE.MathUtils.clamp(config.roughness ?? 0.14, 0.0, 1.0);
    const clampedMetalness = THREE.MathUtils.clamp(config.metalness ?? 0.04, 0.0, 1.0);
    const clampedOpacity = THREE.MathUtils.clamp(config.opacity ?? 1.0, 0.25, 1.0);
    const isTranslucent = clampedOpacity < 0.99 || isGlass;

    mat.roughness = clampedRoughness;
    mat.metalness = clampedMetalness;
    mat.transparent = isTranslucent;
    mat.opacity = isGlass ? 1.0 : clampedOpacity; // For Glass/LiquidGlass, alpha is modulated per-pixel by Fresnel in shader
    mat.depthWrite = !isTranslucent;

    // Update glass uniforms in-place (zero allocation)
    this.glassUniforms.uAccentColor.value.set(accentColor);
    this.glassUniforms.uBodyColor.value.set(bodyColor);
    this.glassUniforms.uGlowBoost.value = config.enableGlow
      ? THREE.MathUtils.clamp(config.glowIntensity ?? 0.55, 0.05, 1.6)
      : 0.25;

    if (isLiquid) {
      this.glassUniforms.uGlassMode.value = 2.0;
      this.glassUniforms.uCoreOpacity.value = THREE.MathUtils.clamp(clampedOpacity * 0.56, 0.30, 0.62);
      this.glassUniforms.uRimOpacity.value = THREE.MathUtils.clamp(clampedOpacity * 0.94 + 0.28, 0.84, 0.97);

      mat.roughness = Math.min(clampedRoughness, 0.06);
      mat.metalness = Math.min(clampedMetalness, 0.12);
      mat.clearcoat = 1.0;
      mat.clearcoatRoughness = 0.015;
      mat.ior = 1.72;
      mat.reflectivity = 1.0;
      mat.iridescence = 0.52;
      mat.iridescenceIOR = 1.42;
      mat.sheen = 0.65;
      mat.sheenRoughness = 0.16;
      mat.envMapIntensity = 1.55;
    } else if (isGlass) {
      this.glassUniforms.uGlassMode.value = 1.0;
      this.glassUniforms.uCoreOpacity.value = THREE.MathUtils.clamp(clampedOpacity * 0.46, 0.24, 0.54);
      this.glassUniforms.uRimOpacity.value = THREE.MathUtils.clamp(clampedOpacity * 0.88 + 0.24, 0.78, 0.94);

      mat.roughness = Math.min(clampedRoughness, 0.08);
      mat.metalness = Math.min(clampedMetalness, 0.08);
      mat.clearcoat = 1.0;
      mat.clearcoatRoughness = 0.03;
      mat.ior = 1.56;
      mat.reflectivity = 0.98;
      mat.iridescence = 0.18;
      mat.sheen = 0.45;
      mat.sheenRoughness = 0.20;
      mat.envMapIntensity = 1.40;
    } else if (isMetallic) {
      this.glassUniforms.uGlassMode.value = 0.0;
      mat.clearcoat = 0.65;
      mat.clearcoatRoughness = 0.12;
      mat.ior = 1.6;
      mat.reflectivity = 1.0;
      mat.iridescence = 0.08;
      mat.sheen = 0.25;
      mat.sheenRoughness = 0.3;
      mat.envMapIntensity = 1.25;
    } else if (isSoft) {
      this.glassUniforms.uGlassMode.value = 0.0;
      mat.clearcoat = 0.22;
      mat.clearcoatRoughness = 0.45;
      mat.ior = 1.42;
      mat.reflectivity = 0.55;
      mat.iridescence = 0.0;
      mat.sheen = 0.85;
      mat.sheenRoughness = 0.65;
      mat.envMapIntensity = 0.85;
    } else if (isMatte) {
      this.glassUniforms.uGlassMode.value = 0.0;
      mat.clearcoat = 0.05;
      mat.clearcoatRoughness = 0.75;
      mat.ior = 1.38;
      mat.reflectivity = 0.35;
      mat.iridescence = 0.0;
      mat.sheen = 0.15;
      mat.sheenRoughness = 0.8;
      mat.envMapIntensity = 0.65;
    } else {
      // Classic glossy electric-blue (unchanged)
      this.glassUniforms.uGlassMode.value = 0.0;
      mat.clearcoat = 1.0;
      mat.clearcoatRoughness = 0.08;
      mat.ior = 1.48;
      mat.reflectivity = 0.95;
      mat.iridescence = 0.0;
      mat.sheen = 0.35;
      mat.sheenRoughness = 0.25;
      mat.envMapIntensity = 1.0;
    }

    this.tempSheen.set(accentColor);
    mat.sheenColor.copy(this.tempSheen);

    // Avoid excessive emissive washout on Glass/LiquidGlass so internal refraction & Fresnel rim stand out
    this.tempEmissive.set(config.enableGlow && !isGlass ? accentColor : bodyColor);
    mat.emissive.copy(this.tempEmissive);
    if (isGlass) {
      mat.emissiveIntensity = 0.08;
    } else {
      mat.emissiveIntensity = config.enableGlow
        ? THREE.MathUtils.clamp(config.glowIntensity ?? 0.45, 0.05, 1.5) * 0.65
        : 0.18;
    }

    mat.needsUpdate = true;

    // 3. Keep eyes 100% opaque, sharp, and readable across all presets
    const eyeMat = this.model.leftEyeMesh.material;
    eyeMat.transparent = false;
    eyeMat.opacity = 1.0;
    eyeMat.depthWrite = true;

    switch (config.eyeStyle) {
      case 'Cyber':
        eyeMat.color.set('#F0F9FF');
        eyeMat.emissive.set(accentColor);
        eyeMat.emissiveIntensity = 0.55;
        break;
      case 'Warm':
        eyeMat.color.set('#FFFBEB');
        eyeMat.emissive.set('#FDE68A');
        eyeMat.emissiveIntensity = 0.32;
        break;
      case 'Starry':
        eyeMat.color.set('#FFFFFF');
        eyeMat.emissive.set('#C4B5FD');
        eyeMat.emissiveIntensity = 0.44;
        break;
      default:
        eyeMat.color.set('#FFFFFF');
        eyeMat.emissive.set('#E0F2FE');
        eyeMat.emissiveIntensity = 0.22;
        break;
    }
    eyeMat.needsUpdate = true;
  }
}
