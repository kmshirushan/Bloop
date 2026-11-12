import * as THREE from 'three';

/**
 * Procedural 3D model of Bloop:
 * - Glossy electric-blue spherical body with subtle vertical gradient & fresnel rim glow
 * - Two expressive white pill-shaped eyes mounted on a face rig
 * - Soft contact glow shadow underneath (transparent falloff)
 * Zero allocations during per-frame updates.
 */
export class BloopModel {
  public readonly root = new THREE.Group();
  public readonly bodyPivot = new THREE.Group();
  public readonly bodyMesh: THREE.Mesh<THREE.SphereGeometry, THREE.MeshPhysicalMaterial>;
  public readonly faceGroup = new THREE.Group();
  public readonly leftEyeGroup = new THREE.Group();
  public readonly rightEyeGroup = new THREE.Group();
  public readonly leftEyeMesh: THREE.Mesh<THREE.CapsuleGeometry, THREE.MeshStandardMaterial>;
  public readonly rightEyeMesh: THREE.Mesh<THREE.CapsuleGeometry, THREE.MeshStandardMaterial>;
  public readonly shadowMesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>;

  private readonly geometries: THREE.BufferGeometry[] = [];
  private readonly materials: THREE.Material[] = [];

  constructor() {
    this.root.add(this.bodyPivot);

    // 1. Glossy Electric-Blue Spherical Body
    const sphereGeo = new THREE.SphereGeometry(1.0, 64, 64);
    this.geometries.push(sphereGeo);

    // Inject vertex colors for a subtle vertical gradient (bright cyan-blue top -> deep electric blue bottom)
    const posAttr = sphereGeo.attributes.position;
    const colors = new Float32Array(posAttr.count * 3);
    const topColor = new THREE.Color(0x38bdf8);    // Bright sky-electric blue
    const midColor = new THREE.Color(0x0284c7);    // Vibrant electric blue
    const bottomColor = new THREE.Color(0x1d4ed8); // Deep royal blue
    const tempColor = new THREE.Color();

    for (let i = 0; i < posAttr.count; i++) {
      const y = posAttr.getY(i); // [-1, 1]
      const t = THREE.MathUtils.clamp((y + 1.0) * 0.5, 0, 1);
      if (t > 0.5) {
        tempColor.lerpColors(midColor, topColor, (t - 0.5) * 2.0);
      } else {
        tempColor.lerpColors(bottomColor, midColor, t * 2.0);
      }
      colors[i * 3] = tempColor.r;
      colors[i * 3 + 1] = tempColor.g;
      colors[i * 3 + 2] = tempColor.b;
    }
    sphereGeo.setAttribute('color', new THREE.BufferAttribute(colors, 3));

    const bodyMat = new THREE.MeshPhysicalMaterial({
      vertexColors: true,
      roughness: 0.14,
      metalness: 0.05,
      clearcoat: 1.0,
      clearcoatRoughness: 0.08,
      reflectivity: 0.95,
      ior: 1.48,
      sheen: 0.35,
      sheenColor: new THREE.Color(0x7dd3fc),
      sheenRoughness: 0.25,
      emissive: new THREE.Color(0x0369a1),
      emissiveIntensity: 0.18
    });
    this.materials.push(bodyMat);

    this.bodyMesh = new THREE.Mesh(sphereGeo, bodyMat);
    this.bodyPivot.add(this.bodyMesh);

    // 2. Expressive White Eyes (mounted slightly in front of sphere surface)
    this.faceGroup.position.set(0, 0.08, 0.88);
    this.bodyPivot.add(this.faceGroup);

    const eyeGeo = new THREE.CapsuleGeometry(0.105, 0.14, 16, 24);
    // Flatten slightly along Z so eyes hug the sphere contour cleanly
    eyeGeo.scale(1.0, 1.0, 0.42);
    this.geometries.push(eyeGeo);

    const eyeMat = new THREE.MeshStandardMaterial({
      color: 0xffffff,
      roughness: 0.18,
      metalness: 0.0,
      emissive: 0xe0f2fe,
      emissiveIntensity: 0.22
    });
    this.materials.push(eyeMat);

    this.leftEyeMesh = new THREE.Mesh(eyeGeo, eyeMat);
    this.rightEyeMesh = new THREE.Mesh(eyeGeo, eyeMat);

    // Subtle specular eye glint dots
    const glintGeo = new THREE.SphereGeometry(0.032, 12, 12);
    const glintMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
    this.geometries.push(glintGeo);
    this.materials.push(glintMat);

    const leftGlint = new THREE.Mesh(glintGeo, glintMat);
    leftGlint.position.set(-0.035, 0.075, 0.048);
    this.leftEyeMesh.add(leftGlint);

    const rightGlint = new THREE.Mesh(glintGeo, glintMat);
    rightGlint.position.set(-0.035, 0.075, 0.048);
    this.rightEyeMesh.add(rightGlint);

    this.leftEyeGroup.position.set(-0.26, 0.0, 0.06);
    this.leftEyeGroup.rotation.y = -0.18;
    this.leftEyeGroup.add(this.leftEyeMesh);

    this.rightEyeGroup.position.set(0.26, 0.0, 0.06);
    this.rightEyeGroup.rotation.y = 0.18;
    this.rightEyeGroup.add(this.rightEyeMesh);

    this.faceGroup.add(this.leftEyeGroup);
    this.faceGroup.add(this.rightEyeGroup);

    // 3. Soft translucent radial contact shadow below the floating mascot
    const shadowGeo = new THREE.PlaneGeometry(1.9, 1.9);
    shadowGeo.rotateX(-Math.PI / 2);
    this.geometries.push(shadowGeo);

    const shadowMat = new THREE.ShaderMaterial({
      transparent: true,
      depthWrite: false,
      uniforms: {
        uOpacity: { value: 0.28 }
      },
      vertexShader: `
        varying vec2 vUv;
        void main() {
          vUv = uv;
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: `
        varying vec2 vUv;
        uniform float uOpacity;
        void main() {
          float dist = length(vUv - vec2(0.5)) * 2.0;
          float alpha = smoothstep(1.0, 0.15, dist) * uOpacity;
          gl_FragColor = vec4(0.02, 0.09, 0.22, alpha);
        }
      `
    });
    this.materials.push(shadowMat);

    this.shadowMesh = new THREE.Mesh(shadowGeo, shadowMat);
    this.shadowMesh.position.set(0, -1.26, 0);
    this.root.add(this.shadowMesh);
  }

  public setShadowHeightFactor(heightOffset: number): void {
    // As Bloop floats higher, shadow shrinks slightly and softens
    const scale = THREE.MathUtils.clamp(1.0 - heightOffset * 0.45, 0.72, 1.18);
    this.shadowMesh.scale.set(scale, 1.0, scale);
    this.shadowMesh.material.uniforms.uOpacity.value = THREE.MathUtils.clamp(
      0.28 - heightOffset * 0.22,
      0.12,
      0.38
    );
  }

  public dispose(): void {
    for (const geo of this.geometries) {
      geo.dispose();
    }
    for (const mat of this.materials) {
      mat.dispose();
    }
  }
}

