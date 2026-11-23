// A2UI Model3D viewer. Runs inside the sandboxed (allow-scripts, opaque-origin) iframe built by
// Components/Meta/A2ui/A2uiModel3D.razor.js. Classic script: window.DysonThree comes from dyson-three-0.170.0.min.js.
// No network: the parent posts the model bytes (transferred ArrayBuffer). Renders on demand, no environment,
// soft light from every direction; the modal posts temperature/intensity.
(function () {
    'use strict';

    var MAX_TRIANGLES = 2000000; // ponytail: hard cap; upgrade path is a meshopt simplify/LOD pass or a setting.
    var FOV = 35;
    var statusEl = document.getElementById('status');
    var parentWin = window.parent;

    function post(msg) {
        parentWin.postMessage(msg, '*');
    }

    function setStatus(state, message) {
        statusEl.textContent = message || '';
        statusEl.hidden = !message;
        post({ type: 'dyson-model3d-status', state: state, message: message || '' });
    }

    var D = window.DysonThree;
    var THREE = D && D.THREE;
    if (!THREE) {
        setStatus('error', '3D viewer failed to load.');
        return;
    }

    var renderer;
    try {
        renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    } catch (e) {
        setStatus('error', 'WebGL is not available.');
        return;
    }

    renderer.setClearColor(0x000000, 0);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.NeutralToneMapping;
    renderer.toneMappingExposure = 1;
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    document.body.insertBefore(renderer.domElement, statusEl);

    var scene = new THREE.Scene();
    var camera = new THREE.PerspectiveCamera(FOV, 1, 0.01, 1000);
    camera.position.set(0, 0, 5);
    scene.add(camera);

    // Lighting (plan 373 §7): hemisphere + ambient + four tetrahedral directionals that follow the camera.
    var hemi = new THREE.HemisphereLight(0xffffff, 0xb0b0b0, 1.2);
    var ambient = new THREE.AmbientLight(0xffffff, 0.4);
    scene.add(hemi, ambient);
    var dirs = [[1, 1, 1], [-1, 1, -1], [-1, -1, 1], [1, -1, -1]].map(function (d) {
        var light = new THREE.DirectionalLight(0xffffff, 0.5);
        light.position.set(d[0], d[1], d[2]).normalize();
        // Target at the camera origin: the light direction is fixed in camera space.
        camera.add(light);
        camera.add(light.target);
        return light;
    });

    var controls = new D.OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.enableZoom = false;
    controls.enablePan = false;

    var model = null;
    var pending = false;

    function render() {
        pending = false;
        if (controls.update())
            requestRender();
        renderer.render(scene, camera);
    }

    function requestRender() {
        if (!pending) {
            pending = true;
            requestAnimationFrame(render);
        }
    }

    controls.addEventListener('change', requestRender);

    function resize() {
        var w = Math.max(1, window.innerWidth);
        var h = Math.max(1, window.innerHeight);
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        requestRender();
    }

    window.addEventListener('resize', resize);
    resize();

    // Tanner Helland kelvin -> sRGB approximation (0..1 per channel).
    function kelvinToRgb(kelvin) {
        var t = Math.min(40000, Math.max(1000, kelvin)) / 100;
        var r, g, b;
        if (t <= 66) {
            r = 255;
            g = 99.4708025861 * Math.log(t) - 161.1195681661;
        } else {
            r = 329.698727446 * Math.pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.pow(t - 60, -0.0755148492);
        }
        if (t >= 66) b = 255;
        else if (t <= 19) b = 0;
        else b = 138.5177312231 * Math.log(t - 10) - 305.0447927307;
        function c(v) { return Math.min(255, Math.max(0, v)) / 255; }
        return [c(r), c(g), c(b)];
    }

    function setLight(kelvin, intensity) {
        var rgb = kelvinToRgb(Number(kelvin) || 6500);
        var k = Math.min(3, Math.max(0.1, Number(intensity) || 1));
        hemi.color.setRGB(rgb[0], rgb[1], rgb[2], THREE.SRGBColorSpace);
        hemi.groundColor.setRGB(rgb[0] * 0.73, rgb[1] * 0.73, rgb[2] * 0.73, THREE.SRGBColorSpace);
        hemi.intensity = 1.2 * k;
        ambient.color.setRGB(rgb[0], rgb[1], rgb[2], THREE.SRGBColorSpace);
        ambient.intensity = 0.4 * k;
        dirs.forEach(function (light) {
            light.color.setRGB(rgb[0], rgb[1], rgb[2], THREE.SRGBColorSpace);
            light.intensity = 0.5 * k;
        });
        requestRender();
    }

    setLight(6500, 1);

    function disposeObject(root) {
        root.traverse(function (o) {
            if (o.geometry) o.geometry.dispose();
            var mats = Array.isArray(o.material) ? o.material : (o.material ? [o.material] : []);
            mats.forEach(function (m) {
                Object.keys(m).forEach(function (k) {
                    if (m[k] && m[k].isTexture) m[k].dispose();
                });
                m.dispose();
            });
        });
    }

    function triangles(root) {
        var n = 0;
        root.traverse(function (o) {
            var g = o.isMesh && o.geometry;
            if (!g) return;
            if (g.index) n += g.index.count / 3;
            else if (g.attributes.position) n += g.attributes.position.count / 3;
        });
        return Math.round(n);
    }

    function show(root) {
        if (model) {
            scene.remove(model);
            disposeObject(model);
            model = null;
        }

        var count = triangles(root);
        if (count > MAX_TRIANGLES) {
            disposeObject(root);
            setStatus('error', 'Model has ' + count.toLocaleString('en-US') + ' triangles; the limit is 2,000,000.');
            requestRender();
            return;
        }

        root.updateMatrixWorld(true);
        var box = new THREE.Box3().setFromObject(root);
        if (box.isEmpty()) {
            disposeObject(root);
            setStatus('error', 'Model has no geometry.');
            return;
        }

        var center = box.getCenter(new THREE.Vector3());
        root.position.sub(center);
        var r = box.getBoundingSphere(new THREE.Sphere()).radius || 1;
        var dist = (r / Math.sin(THREE.MathUtils.degToRad(FOV / 2))) * 1.15;
        camera.near = Math.max(dist / 1000, r / 100);
        camera.far = dist + r * 20;
        camera.position.copy(new THREE.Vector3(0.5, 0.35, 1).normalize().multiplyScalar(dist));
        camera.updateProjectionMatrix();
        controls.target.set(0, 0, 0);
        controls.minDistance = r * 0.2;
        controls.maxDistance = dist * 10;
        controls.update();

        model = root;
        scene.add(root);
        setStatus('loaded', '');
        requestRender();
    }

    function fail(e) {
        setStatus('error', 'Could not load the model: ' + ((e && e.message) || String(e)));
    }

    function load(format, buffer, error) {
        if (error || !buffer) {
            setStatus('error', error || 'No model data.');
            return;
        }
        setStatus('loading', 'Loading 3D model…');
        try {
            if (format === 'fbx') {
                show(new D.FBXLoader().parse(buffer, ''));
            } else {
                new D.GLTFLoader().parse(buffer, '', function (gltf) { show(gltf.scene); }, fail);
            }
        } catch (e) {
            fail(e);
        }
    }

    window.addEventListener('message', function (event) {
        if (event.source !== parentWin) return;
        var d = event.data;
        if (!d || typeof d.type !== 'string') return;
        if (d.type === 'dyson-model3d-load') {
            load(d.format, d.buffer, d.error);
        } else if (d.type === 'dyson-model3d-light') {
            setLight(d.kelvin, d.intensity);
        } else if (d.type === 'dyson-model3d-mode') {
            var inline = d.inline !== false;
            controls.enableZoom = !inline;
            controls.enablePan = !inline;
        }
    });

    // Focus inside the iframe swallows keys: forward Escape so the modal still closes.
    window.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') post({ type: 'dyson-model3d-escape' });
    });

    post({ type: 'dyson-model3d-ready' });
})();
