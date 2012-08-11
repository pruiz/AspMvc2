namespace Microsoft.Web.Mvc.Resources {
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;
    using System.Net.Mime;
    using System.Text;
    using System.Web;
    using System.Web.Mvc;
    using System.Web.Routing;

    /// <summary>
    /// Specialized ControllerFactory that augments the base controller factory to make it RESTful - specifically, adding
    /// support for multiple formats, HTTP method based dispatch to controller methods and HTTP error handling
    /// </summary>
    public class ResourceControllerFactory : IControllerFactory {
        const string restActionToken = "$REST$";

        IControllerFactory inner;

        public ResourceControllerFactory() {
            this.inner = ControllerBuilder.Current.GetControllerFactory();
        }

        public ResourceControllerFactory(IControllerFactory inner) {
            this.inner = inner;
        }

        public IController CreateController(RequestContext requestContext, string controllerName) {
            IController ic = this.inner.CreateController(requestContext, controllerName);
            Controller c = ic as Controller;
            if (c != null && WebApiEnabledAttribute.IsDefined(c)) {
                IActionInvoker iai = c.ActionInvoker;
                ControllerActionInvoker cai = iai as ControllerActionInvoker;
                if (cai != null) {
                    c.ActionInvoker = new ResourceControllerActionInvoker();

                    string actionName = requestContext.RouteData.Values["action"] as string;
                    if (string.IsNullOrEmpty(actionName)) {
                        // set it to a well known dummy value to avoid not having an action as that would prevent the fixup
                        // code in ResourceControllerActionInvoker, which is based on ActionDescriptor, from running
                        requestContext.RouteData.Values["action"] = restActionToken;
                    }
                }
            }
            return ic;
        }

        public void ReleaseController(IController controller) {
            this.inner.ReleaseController(controller);
        }

        // This ActionInvoker allows us to dispatch to a controller when no action was provided by the routing
        // infrastructure, but the information is available in the request's HTTP verb (GET/PUT/POST/DELETE)
        class ResourceControllerActionInvoker : ControllerActionInvoker {
            public ResourceControllerActionInvoker() {
            }

            protected override ActionDescriptor FindAction(ControllerContext controllerContext, ControllerDescriptor controllerDescriptor, string actionName) {
                if (actionName == restActionToken) {
                    // cleanup the restActionToken we set earlier
                    controllerContext.RequestContext.RouteData.Values["action"] = null;

                    List<ActionDescriptor> matches = new List<ActionDescriptor>();
                    foreach (ActionDescriptor ad in controllerDescriptor.GetCanonicalActions()) {
                        object[] acceptVerbs = ad.GetCustomAttributes(typeof(AcceptVerbsAttribute), false);
                        if (acceptVerbs.Length > 0) {
                            foreach (object o in acceptVerbs) {
                                AcceptVerbsAttribute ava = o as AcceptVerbsAttribute;
                                if (ava != null) {
                                    if (ava.Verbs.Contains(controllerContext.HttpContext.Request.GetHttpMethodOverride().ToUpperInvariant())) {
                                        matches.Add(ad);
                                    }
                                }
                            }
                        }
                    }
                    switch (matches.Count) {
                        case 0:
                            break;
                        case 1:
                            ActionDescriptor ad = matches[0];
                            actionName = ad.ActionName;
                            controllerContext.RequestContext.RouteData.Values["action"] = actionName;
                            return ad;
                        default:
                            StringBuilder matchesString = new StringBuilder(matches[0].ActionName);
                            for (int index = 1; index < matches.Count; index++) {
                                matchesString.Append(", ");
                                matchesString.Append(matches[index].ActionName);
                            }
                            return new ResourceErrorActionDescriptor(controllerDescriptor, HttpStatusCode.Conflict, string.Format(CultureInfo.CurrentCulture, "Error dispatching on controller {0}, conflicting actions matched: (1)", controllerDescriptor.ControllerName, matchesString.ToString()));
                    }
                }
                return base.FindAction(controllerContext, controllerDescriptor, actionName) ??
                    new ResourceErrorActionDescriptor(controllerDescriptor, HttpStatusCode.NotFound, string.Format(CultureInfo.CurrentCulture, "Error dispatching on controller {0}, no actions matched", controllerDescriptor.ControllerName));
            }

            // This class is used when we don't find an ActionDescriptor or find multiple matches
            // in this case we want to return an error response but throwing an HttpException from
            // FindAction bypasses the InvokeExceptionFilters, so instead we throw in this custom ActionDescriptor
            class ResourceErrorActionDescriptor : ActionDescriptor {
                ControllerDescriptor controllerDescriptor;
                string message;
                HttpStatusCode statusCode;

                public ResourceErrorActionDescriptor(ControllerDescriptor controllerDescriptor, HttpStatusCode statusCode, string message) {
                    this.message = message;
                    this.statusCode = statusCode;
                    this.controllerDescriptor = controllerDescriptor;
                }

                public override string ActionName {
                    get { return restActionToken; }
                }

                public override ControllerDescriptor ControllerDescriptor {
                    get { return this.controllerDescriptor; }
                }

                public override object Execute(ControllerContext controllerContext, IDictionary<string, object> parameters) {
                    HttpException he = new HttpException((int)this.statusCode, this.message);
                    ResourceErrorActionResult rear;
                    if (!WebApiEnabledAttribute.TryGetErrorResult2(controllerContext.RequestContext, he, out rear)) {
                        rear = new ResourceErrorActionResult(new HttpException((int)this.statusCode, this.message), new ContentType("text/plain"));
                    }
                    return rear;
                }

                public override ParameterDescriptor[] GetParameters() {
                    return new ParameterDescriptor[0];
                }
            }
        }
    }
}
