import * as THREE from 'three';

/**
 * Configures three-point studio lighting and a multi-panel procedural PMREM glass studio
 * environment map (overhead softbox, horizon band, dual cyan/sky rim reflectors, and
 * underside caustic bounce) so Bloop's Classic and LiquidGlass materials have rich,
 * curved specular reflections without external HDR file dependencies.
 */
export class Lighting {
  public readonly group = new THREE.Group();
  private envMapTexture: THREE.Texture | null = null;

  constructor(scene: THREE.Scene, renderer: THREE.WebGLRenderer) {
    // 1. Soft hemisphere ambient fill (sky blue top, deep indigo bottom)
    const hemiLight = new THREE.HemisphereLight(0x93c5fd, 0x0f172a, 1.35);
    hemiLight.position.set(0, 4, 0);
    this.group.add(hemiLight);

    // 2. Main key light (top-left front) for primary specular highlight
    const keyLight = new THREE.DirectionalLight(0xffffff, 2.55);
    keyLight.position.set(-2.4, 3.2, 4.0);
    this.group.add(keyLight);

    // 3. Cool cyan rim light (back-right) for spherical edge definition
    const rimLight = new THREE.DirectionalLight(0x38bdf8, 2.2);
    rimLight.position.set(3.0, 2.0, -3.0);
    this.group.add(rimLight);

    // 4. Subtle bottom bounce light for glossy underside lift
    const bounceLight = new THREE.PointLight(0x60a5fa, 1.15, 8.0);
    bounceLight.position.set(0.0, -2.5, 2.2);
    this.group.add(bounceLight);

    scene.add(this.group);

    // Generate procedural studio environment for clearcoat & LiquidGlass reflections
    this.envMapTexture = this.createProceduralEnvironment(renderer);
    scene.environment = this.envMapTexture;
  }

  private createProceduralEnvironment(renderer: THREE.WebGLRenderer): THREE.Texture {
    const pmremGenerator = new THREE.PMREMGenerator(renderer);
    pmremGenerator.compileEquirectangularShader();

    const envScene = new THREE.Scene();
    envScene.background = new THREE.Color(0x061020);

    // 1. Curved studio horizon ring for continuous liquid-glass equatorial reflection
    const horizonGeo = new THREE.CylinderGeometry(9, 9, 2.2, 48, 1, true);
    const horizonMat = new THREE.MeshBasicMaterial({
      color: 0x0e2a4d,
      side: THREE.BackSide
    });
    const horizonMesh = new THREE.Mesh(horizonGeo, horizonMat);
    horizonMesh.position.set(0, -0.4, 0);
    envScene.add(horizonMesh);

    // 2. Primary overhead softbox (top-left front) for crisp glass highlight arc
    const mainSoftboxGeo = new THREE.PlaneGeometry(5.2, 3.4);
    const mainSoftboxMat = new THREE.MeshBasicMaterial({
      color: 0xffffff,
      side: THREE.DoubleSide
    });
    const mainSoftbox = new THREE.Mesh(mainSoftboxGeo, mainSoftboxMat);
    mainSoftbox.position.set(-3.2, 4.4, 4.2);
    mainSoftbox.lookAt(0, 0, 0);
    envScene.add(mainSoftbox);

    // 3. Secondary cool-sky fill strip (top-right)
    const secondaryGeo = new THREE.PlaneGeometry(4.2, 2.4);
    const secondaryMat = new THREE.MeshBasicMaterial({
      color: 0xbae6fd,
      side: THREE.DoubleSide
    });
    const secondaryBox = new THREE.Mesh(secondaryGeo, secondaryMat);
    secondaryBox.position.set(3.8, 3.2, 2.6);
    secondaryBox.lookAt(0, 0, 0);
    envScene.add(secondaryBox);

    // 4. Back-right electric cyan rim panel
    const rimPanelGeo = new THREE.PlaneGeometry(5.0, 4.2);
    const rimPanelMat = new THREE.MeshBasicMaterial({
      color: 0x38bdf8,
      side: THREE.DoubleSide
    });
    const rimPanel = new THREE.Mesh(rimPanelGeo, rimPanelMat);
    rimPanel.position.set(4.2, 1.4, -3.5);
    rimPanel.lookAt(0, 0, 0);
    envScene.add(rimPanel);

    // 5. Underside sapphire caustic bounce disc
    const floorGeo = new THREE.CircleGeometry(5.5, 32);
    const floorMat = new THREE.MeshBasicMaterial({
      color: 0x0284c7,
      side: THREE.DoubleSide
    });
    const floorDisc = new THREE.Mesh(floorGeo, floorMat);
    floorDisc.rotation.x = -Math.PI / 2;
    floorDisc.position.set(0, -5.0, 0.8);
    envScene.add(floorDisc);

    const renderTarget = pmremGenerator.fromScene(envScene, 0.035);
    pmremGenerator.dispose();
    horizonGeo.dispose();
    horizonMat.dispose();
    mainSoftboxGeo.dispose();
    mainSoftboxMat.dispose();
    secondaryGeo.dispose();
    secondaryMat.dispose();
    rimPanelGeo.dispose();
    rimPanelMat.dispose();
    floorGeo.dispose();
    floorMat.dispose();

    return renderTarget.texture;
  }

  public dispose(): void {
    this.envMapTexture?.dispose();
  }
}
