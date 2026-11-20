Política de privacidad de DraftOptimizer
Última actualización — 20 de noviembre de 2026

DraftOptimizer es una aplicación de escritorio para Windows que ofrece recomendaciones estadísticas durante la selección de campeones de League of Legends y permite administrar builds, runas, objetos y hechizos de invocador.

El responsable del proyecto es MatiasV2. Las consultas sobre privacidad y las solicitudes relacionadas con datos personales pueden enviarse a matias.faundez@outlook.cl.
1. Información utilizada
Información del cliente de League of Legends
La aplicación consulta la API local del cliente para conocer el estado de la selección, los campeones seleccionados o baneados, las posiciones asignadas, los favoritos y los identificadores necesarios para aplicar configuraciones a la cuenta activa.

Esta información se utiliza para calcular recomendaciones y ejecutar las funciones que el usuario haya habilitado, incluidas la importación de configuraciones y las opciones de selección y baneo automáticos.
Cuenta de DraftOptimizer
El registro y el inicio de sesión opcionales se gestionan mediante Firebase Authentication. Este servicio procesa el correo electrónico, la contraseña introducida para autenticarse y los datos técnicos necesarios para mantener la cuenta.

La aplicación conserva un token de sesión en el Administrador de credenciales de Windows para permitir el inicio de sesión posterior.
Preferencias y perfiles
La aplicación guarda localmente los perfiles de builds, runas, objetos y hechizos, los campeones favoritos, las prioridades de baneo y las preferencias de funcionamiento.

Cuando el usuario inicia sesión, estas configuraciones se sincronizan con Cloud Firestore y quedan asociadas al identificador de su cuenta de Firebase.
2. Conexiones con servicios externos
DraftOptimizer realiza consultas a los siguientes servicios para proporcionar sus funciones.
- Google Firebase, para autenticación, sincronización, configuración remota y distribución del dataset compartido.
- Lolalytics, para estadísticas y recomendaciones de builds.
- DraftGap, para obtener el catálogo utilizado por la aplicación.
- Riot Games Data Dragon, para imágenes, nombres y otros recursos del juego.
Estas conexiones pueden comunicar a los proveedores la dirección IP, la fecha de la solicitud y los recursos solicitados, como el campeón, la posición, el rango o la ventana estadística.

El tratamiento realizado por Google se describe en su política de privacidad y en la información de privacidad y seguridad de Firebase.
3. Dataset estadístico compartido
La aplicación descarga públicamente un dataset de estadísticas agregadas de campeones en Diamante+, considerando los últimos siete días.

Este dataset se actualiza mediante una cuenta administradora, con un intervalo mínimo de 24 horas entre publicaciones. Su contenido corresponde a estadísticas de campeones, enfrentamientos y sinergias.

La descarga funciona sin crear una cuenta. La aplicación conserva una copia local para utilizarla cuando la conexión esté interrumpida.
4. Registros de errores
La aplicación genera archivos TXT locales cuando registra fallos de funcionamiento.

Estos archivos pueden incluir fechas, versión de la aplicación, arquitectura del proceso, operación realizada, mensajes de error, excepciones internas y trazas técnicas. Según el error, también pueden aparecer rutas del equipo, nombres de archivos o identificadores relacionados con la operación.

Los registros se almacenan en la carpeta local de datos de DraftOptimizer. El usuario puede revisarlos, eliminarlos y compartirlos voluntariamente para solicitar soporte. Antes de compartirlos, debe revisar su contenido y ocultar cualquier información que considere privada.
5. Finalidad y acceso a los datos
Los datos se utilizan para prestar las funciones de la aplicación, mantener las preferencias, sincronizar perfiles, distribuir estadísticas y diagnosticar problemas.

Los datos sincronizados se alojan en la infraestructura de Firebase. El responsable del proyecto puede acceder a ellos mediante las herramientas administrativas necesarias para mantener el servicio y atender solicitudes de soporte o eliminación.

Los servicios externos pueden procesar información en distintos países, según su infraestructura y sus condiciones de servicio.
6. Conservación y eliminación
Los archivos locales permanecen en el equipo hasta que el usuario los elimine. Desinstalar o borrar la carpeta del programa puede conservar los datos guardados por separado en la carpeta de datos de Windows.

Los datos de cuenta y sincronización se conservan mientras se mantenga la cuenta o hasta que se tramite su eliminación. Cerrar sesión elimina la credencial de sesión guardada por la aplicación; los datos sincronizados permanecen disponibles para futuros accesos.

Para solicitar acceso, corrección o eliminación de la cuenta y sus datos, escribe a matias.faundez@outlook.cl desde el correo asociado a la cuenta. Podrá solicitarse una verificación de identidad para proteger la información.

La eliminación de copias de respaldo y registros administrados por los proveedores estará sujeta a sus procedimientos de conservación.
7. Seguridad
La aplicación utiliza conexiones HTTPS para comunicarse con los servicios externos y el almacenamiento de credenciales de Windows para conservar el token de sesión.

La seguridad también depende de las reglas de acceso configuradas en Firebase y de la protección del equipo del usuario.
8. Cambios en esta política
Las modificaciones se publicarán en este documento y se actualizará la fecha indicada al comienzo. La política deberá revisarse cuando se incorporen nuevas funciones o cambie el tratamiento de datos.