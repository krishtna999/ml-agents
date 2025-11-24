bash setup_script.sh
rm -rf asymmetric_poca_unity_build/
unzip unity-builds/asymmetric_poca_unity_build.zip
chmod -R 755 asymmetric_poca_unity_build/asymmetric_poca.x86_64
mlagents-learn config/sac/soccer_sac_defensive_5080.yaml --run-id=defensive_5080 --num-envs=16 --torch-device=cuda --env="asymmetric_poca_unity_build/asymmetric_poca.x86_64" --no-graphics
